namespace Foundgine.Runtime.Recovery;

/// <summary>Where an execution stands after reconciliation.</summary>
public enum MutationReconciliationOutcome
{
    /// <summary>No execution is recorded for the key.</summary>
    NotFound,

    /// <summary>Still within its lease; nothing was touched.</summary>
    InProgress,

    /// <summary>The mutation is durable.</summary>
    Committed,

    /// <summary>The mutation is not durable; the same key may be retried.</summary>
    Aborted,

    /// <summary>Still unknown (no reconciler, an "unknown" verdict, or the reconciler failed). Retry later or resolve manually.</summary>
    Unresolved,
}

/// <summary>Result of reconciling one idempotency key.</summary>
/// <param name="Outcome">Where the execution stands now.</param>
/// <param name="Record">The ledger record after reconciliation (null for <see cref="MutationReconciliationOutcome.NotFound"/>).</param>
/// <param name="Changed">True when this call changed the ledger or acknowledged a pending audit event.</param>
/// <param name="Detail">Short safe text.</param>
public sealed record MutationReconciliationReport(
    MutationReconciliationOutcome Outcome,
    MutationExecutionRecord? Record,
    bool Changed,
    string Detail);
