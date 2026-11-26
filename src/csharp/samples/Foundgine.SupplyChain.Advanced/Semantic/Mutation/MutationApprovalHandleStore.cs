using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Foundgine.SupplyChain.Advanced.Mutation;

/// <summary>Single-use, opaque approval handles. Records never expose the trusted mutation approval payload.</summary>
public sealed class MutationApprovalHandleStore
{
    private sealed record Entry(string Subject, string Tenant, string Role, string Fingerprint,
        string Tool, string Payload, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;

    public MutationApprovalHandleStore(TimeSpan? lifetime = null, int capacity = 512, TimeProvider? clock = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(10);
        if (_lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _clock = clock ?? TimeProvider.System;
    }

    public string Issue(string subject, string tenant, string role, string tool, string payload, string fingerprint)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_entries.Count >= _capacity) throw new InvalidOperationException("The approval store is full.");
            var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var digest = Digest(handle);
            if (!_entries.TryAdd(digest, new(subject, tenant, role, fingerprint, tool, payload, _clock.GetUtcNow() + _lifetime)))
                throw new InvalidOperationException("Could not allocate an approval handle.");
            return handle;
        }
    }

    public (string Tool, string Payload, string Fingerprint) Take(string handle, string subject, string tenant, string role)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle.Length > 256) throw new UnauthorizedAccessException("Approval handle is invalid.");
        var digest = Digest(handle);
        if (!_entries.TryGetValue(digest, out var entry)) throw new InvalidOperationException("Approval is missing, expired or already used.");
        if (entry.ExpiresAt <= _clock.GetUtcNow())
        {
            _entries.TryRemove(digest, out _);
            throw new InvalidOperationException("Approval is missing, expired or already used.");
        }
        if (!FixedEquals(entry.Subject, subject) || !FixedEquals(entry.Tenant, tenant) || !FixedEquals(entry.Role, role))
            throw new UnauthorizedAccessException("Approval belongs to a different caller context.");
        if (!_entries.TryRemove(digest, out _)) throw new InvalidOperationException("Approval is missing, expired or already used.");
        return (entry.Tool, entry.Payload, entry.Fingerprint);
    }

    private void PruneExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var item in _entries)
            if (item.Value.ExpiresAt <= now) _entries.TryRemove(item.Key, out _);
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
