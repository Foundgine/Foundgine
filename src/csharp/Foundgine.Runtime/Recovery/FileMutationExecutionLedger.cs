using System.Text;

namespace Foundgine.Runtime.Recovery;

/// <summary>
/// Durable ledger backed by a lock-protected, append-only JSON-lines journal
/// (<see cref="MutationExecutionRecordJson"/>); the latest line for an identity wins. Every append is flushed to disk
/// before the call returns, so intent recorded before a crash is still there after restart. Each operation takes the
/// lock and reads any lines other processes appended.
/// </summary>
/// <remarks>
/// <para>Single-node / shared-filesystem use; cloud deployments should implement <see cref="IMutationExecutionLedger"/>
/// over a shared transactional store. The journal is never compacted by this class. A torn final line (crash
/// mid-append) is ignored and truncated by the next append; any other unreadable line fails closed with
/// <see cref="MutationLedgerException"/>.</para>
/// <para>The lock is an operating-system exclusive open (<see cref="FileShare.None"/>) of the sidecar
/// <c>.lock</c> file. The OS releases it when its holder dies, so a crashed process never leaves a stale lock and a
/// live holder is never evicted. The lock file is never deleted. Operations are short blocking file I/O; they return
/// completed <see cref="ValueTask"/>s.</para>
/// </remarks>
public sealed class FileMutationExecutionLedger : IMutationExecutionLedger
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly string _lockPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, MutationExecutionRecord> _records = new();
    private readonly List<string> _order = new();
    private long _parsedOffset;

    public FileMutationExecutionLedger(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A ledger path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        try
        {
            var parent = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            using var _ = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException e)
        {
            throw new MutationLedgerException("Unable to initialize the mutation execution ledger.", e);
        }
    }

    public ValueTask<MutationLedgerCreateResult> TryCreateAsync(MutationExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ValueTask.FromResult(Locked(() =>
        {
            if (_records.TryGetValue(record.Identity, out var existing)) return new MutationLedgerCreateResult(false, existing);
            var stored = record.WithVersion(1);
            Append(stored);
            Put(stored);
            return new MutationLedgerCreateResult(true, stored);
        }, cancellationToken));
    }

    public ValueTask<MutationExecutionRecord?> TryUpdateAsync(long expectedVersion, MutationExecutionRecord updated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        return ValueTask.FromResult(Locked<MutationExecutionRecord?>(() =>
        {
            if (!_records.TryGetValue(updated.Identity, out var current) || current.Version != expectedVersion) return null;
            var stored = updated.WithVersion(expectedVersion + 1);
            Append(stored);
            Put(stored);
            return stored;
        }, cancellationToken));
    }

    public ValueTask<MutationExecutionRecord?> FindAsync(string? tenant, string? subject, string idempotencyKey, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Locked<MutationExecutionRecord?>(
            () => _records.GetValueOrDefault(MutationExecutionRecord.MakeIdentity(tenant, subject, idempotencyKey)), cancellationToken));

    public ValueTask<MutationExecutionRecord?> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Locked<MutationExecutionRecord?>(
            () => _order.Select(i => _records[i]).FirstOrDefault(r => r.RequestId == requestId), cancellationToken));

    public ValueTask<IReadOnlyList<MutationExecutionRecord>> ListUnresolvedAsync(int limit, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Locked(() => MutationLedgerRules.Unresolved(_order.Select(i => _records[i]), limit), cancellationToken));

    private void Put(MutationExecutionRecord record)
    {
        if (!_records.ContainsKey(record.Identity)) _order.Add(record.Identity);
        _records[record.Identity] = record;
    }

    private T Locked<T>(Func<T> action, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            try
            {
                using var _ = AcquireLock(cancellationToken);
                Refresh();
                return action();
            }
            catch (IOException e)
            {
                throw new MutationLedgerException("The mutation execution ledger is unavailable.", e);
            }
        }
    }

    /// <summary>Parses journal bytes appended since the last refresh; an unterminated tail is left for <see cref="Append"/>.</summary>
    private void Refresh()
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var size = fs.Length;
        if (size < _parsedOffset)
        {
            _records.Clear();
            _order.Clear();
            _parsedOffset = 0;
        }
        if (size == _parsedOffset) return;
        var bytes = new byte[size - _parsedOffset];
        fs.Position = _parsedOffset;
        fs.ReadExactly(bytes);
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            var line = Encoding.UTF8.GetString(bytes, start, i - start);
            if (!string.IsNullOrWhiteSpace(line))
            {
                try
                {
                    var rec = MutationExecutionRecordJson.FromJson(line);
                    if (!_records.TryGetValue(rec.Identity, out var prior) || rec.Version >= prior.Version) Put(rec);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
                {
                    throw new MutationLedgerException($"Corrupt mutation ledger journal at byte offset {_parsedOffset + start}.", e);
                }
            }
            start = i + 1;
        }
        _parsedOffset += start;
    }

    private void Append(MutationExecutionRecord record)
    {
        var bytes = Encoding.UTF8.GetBytes(MutationExecutionRecordJson.ToJson(record) + "\n");
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        if (fs.Length > _parsedOffset) fs.SetLength(_parsedOffset); // torn tail from a crashed append
        fs.Position = _parsedOffset;
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
        _parsedOffset += bytes.Length;
    }

    private FileStream AcquireLock(CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow - start < LockTimeout)
            {
                Thread.Sleep(10); // held by another process or ledger instance; the OS frees it if the holder dies
            }
            catch (IOException e)
            {
                throw new IOException($"Timed out acquiring the ledger lock '{_lockPath}'.", e);
            }
        }
    }
}
