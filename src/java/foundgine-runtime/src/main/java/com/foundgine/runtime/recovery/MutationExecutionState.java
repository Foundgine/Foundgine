package com.foundgine.runtime.recovery;

/** Lifecycle of one idempotent mutation execution. */
public enum MutationExecutionState {
    /** Intent durably recorded; provider dispatch is pending or in flight. Protected by a lease. */
    STARTED,
    /** The provider reported (or reconciliation proved) that the mutation is durable. Terminal. */
    COMMITTED,
    /** Definitely not applied. Terminal for this attempt; the same key may be retried. */
    ABORTED,
    /** Dispatched, but whether it became durable is unknown. Never retried blindly; must be reconciled. */
    IN_DOUBT;

    public boolean isTerminalOutcome() { return this == COMMITTED || this == ABORTED; }
}
