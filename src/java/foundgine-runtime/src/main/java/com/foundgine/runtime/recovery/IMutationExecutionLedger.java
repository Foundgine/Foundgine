package com.foundgine.runtime.recovery;

import java.util.List;
import java.util.Optional;

/**
 * Durable, write-ahead record of mutation executions keyed by (tenant, subject, idempotency key). The engine records
 * its intent here <em>before</em> dispatching to the provider, so a crash, a lost acknowledgement or a failed audit
 * write can later be resolved from evidence instead of guessed at.
 *
 * <p>Implementations must make {@link #tryCreate} and {@link #tryUpdate} atomic and durable before returning
 * (compare-and-set on {@code version}). Failures are reported with {@link MutationLedgerException}; they must never be
 * swallowed, because the engine treats an unreachable ledger as "do not execute".
 */
public interface IMutationExecutionLedger {

    /** Outcome of {@link #tryCreate}: {@code created} is true when {@code record} is the newly stored record. */
    record CreateResult(boolean created, MutationExecutionRecord record) {}

    /** Stores {@code record} (version 1) if the identity is unused; otherwise returns the existing record. */
    CreateResult tryCreate(MutationExecutionRecord record);

    /**
     * Replaces the stored record for {@code updated}'s identity when its version equals {@code expectedVersion}. Returns
     * the stored record (version + 1), or empty when another writer changed it first.
     */
    Optional<MutationExecutionRecord> tryUpdate(long expectedVersion, MutationExecutionRecord updated);

    Optional<MutationExecutionRecord> find(String tenant, String subject, String idempotencyKey);

    Optional<MutationExecutionRecord> findByRequestId(String requestId);

    /**
     * Records that need attention: {@code STARTED}, {@code IN_DOUBT}, or {@code COMMITTED} with an unacknowledged
     * audit event. Oldest first, at most {@code limit}.
     */
    List<MutationExecutionRecord> listUnresolved(int limit);
}
