package com.foundgine.runtime.recovery;

/**
 * Result of reconciling one idempotency key.
 *
 * @param outcome where the execution stands now
 * @param record the ledger record after reconciliation (null for {@code NOT_FOUND})
 * @param changed true when this call changed the ledger or acknowledged a pending audit event
 * @param detail short safe text
 */
public record MutationReconciliationReport(Outcome outcome, MutationExecutionRecord record, boolean changed,
        String detail) {
    public enum Outcome {
        /** No execution is recorded for the key. */
        NOT_FOUND,
        /** Still within its lease; nothing was touched. */
        IN_PROGRESS,
        /** The mutation is durable. */
        COMMITTED,
        /** The mutation is not durable; the same key may be retried. */
        ABORTED,
        /** Still unknown (no reconciler, an "unknown" verdict, or the reconciler failed). Retry later or resolve manually. */
        UNRESOLVED
    }
}
