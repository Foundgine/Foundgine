namespace Foundgine.Runtime.Recovery;

/// <summary>Process-local ledger for tests and single-node development. Evidence is lost on restart.</summary>
public sealed class InMemoryMutationExecutionLedger : IMutationExecutionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MutationExecutionRecord> _records = new();
    private readonly List<string> _order = new();

    public ValueTask<MutationLedgerCreateResult> TryCreateAsync(MutationExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_records.TryGetValue(record.Identity, out var existing)) return ValueTask.FromResult(new MutationLedgerCreateResult(false, existing));
            var stored = record.WithVersion(1);
            _records[stored.Identity] = stored;
            _order.Add(stored.Identity);
            return ValueTask.FromResult(new MutationLedgerCreateResult(true, stored));
        }
    }

    public ValueTask<MutationExecutionRecord?> TryUpdateAsync(long expectedVersion, MutationExecutionRecord updated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (_gate)
        {
            if (!_records.TryGetValue(updated.Identity, out var current) || current.Version != expectedVersion)
                return ValueTask.FromResult<MutationExecutionRecord?>(null);
            var stored = updated.WithVersion(expectedVersion + 1);
            _records[stored.Identity] = stored;
            return ValueTask.FromResult<MutationExecutionRecord?>(stored);
        }
    }

    public ValueTask<MutationExecutionRecord?> FindAsync(string? tenant, string? subject, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_records.GetValueOrDefault(MutationExecutionRecord.MakeIdentity(tenant, subject, idempotencyKey)));
    }

    public ValueTask<MutationExecutionRecord?> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_order.Select(i => _records[i]).FirstOrDefault(r => r.RequestId == requestId));
    }

    public ValueTask<IReadOnlyList<MutationExecutionRecord>> ListUnresolvedAsync(int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult(MutationLedgerRules.Unresolved(_order.Select(i => _records[i]), limit));
    }
}
