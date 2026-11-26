namespace Foundgine.Runtime.Recovery;

/// <summary>Outcome of <see cref="IMutationExecutionLedger.TryCreateAsync"/>.</summary>
public readonly record struct MutationLedgerCreateResult(bool Created, MutationExecutionRecord Record);

/// <summary>
/// Durable, write-ahead record of mutation executions keyed by (tenant, subject, idempotency key). Port of the Java
/// <c>IMutationExecutionLedger</c>.
/// </summary>
public interface IMutationExecutionLedger
{
    /// <summary>Stores <paramref name="record"/> (version 1) if the identity is unused; otherwise returns the existing record.</summary>
    ValueTask<MutationLedgerCreateResult> TryCreateAsync(MutationExecutionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored record for <paramref name="updated"/>'s identity when its version equals
    /// <paramref name="expectedVersion"/>. Returns the stored record (version + 1), or null when another writer changed it first.
    /// </summary>
    ValueTask<MutationExecutionRecord?> TryUpdateAsync(long expectedVersion, MutationExecutionRecord updated, CancellationToken cancellationToken = default);

    ValueTask<MutationExecutionRecord?> FindAsync(string? tenant, string? subject, string idempotencyKey, CancellationToken cancellationToken = default);

    ValueTask<MutationExecutionRecord?> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that need attention: <c>Started</c>, <c>InDoubt</c>, or <c>Committed</c> with an unacknowledged audit
    /// event. Oldest first, at most <paramref name="limit"/>.
    /// </summary>
    ValueTask<IReadOnlyList<MutationExecutionRecord>> ListUnresolvedAsync(int limit, CancellationToken cancellationToken = default);
}

internal static class MutationLedgerRules
{
    public static bool NeedsAttention(MutationExecutionRecord r) => r.State switch
    {
        MutationExecutionState.Started or MutationExecutionState.InDoubt => true,
        MutationExecutionState.Committed => !r.AuditAcknowledged,
        _ => false,
    };

    public static IReadOnlyList<MutationExecutionRecord> Unresolved(IEnumerable<MutationExecutionRecord> all, int limit)
        => all.Where(NeedsAttention).OrderBy(r => r.StartedAt).Take(Math.Max(0, limit)).ToList(); // OrderBy is stable
}
