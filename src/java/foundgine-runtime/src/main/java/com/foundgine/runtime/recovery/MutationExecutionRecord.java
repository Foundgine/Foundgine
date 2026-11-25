package com.foundgine.runtime.recovery;

import com.foundgine.core.execution.ExecutionReceipt;
import java.time.Instant;
import java.util.List;
import java.util.Objects;

/**
 * Durable evidence for one idempotent mutation execution. It stores identifiers, fingerprints, the receipt and
 * per-operation affected-row counts, never request payloads or returned field values.
 *
 * @param tenant tenant of the idempotency scope (null when the caller has no security context)
 * @param subject subject of the idempotency scope (null when the caller has no security context)
 * @param idempotencyKey caller-supplied key, {@code [A-Za-z0-9._:-]{1,128}}
 * @param requestId engine-assigned execution id (the receipt's request id)
 * @param version optimistic-concurrency version, assigned by the ledger (1 on creation, +1 per update)
 * @param leaseExpiresAt while {@code STARTED}, the earliest time reconciliation may treat the execution as abandoned
 * @param receipt provisional ({@code pending}) while unresolved, final ({@code succeeded}/{@code reconciled}) once committed
 * @param affectedRows per-operation affected rows when known (empty when committed through reconciliation)
 * @param auditAcknowledged whether the success audit event was acknowledged by the audit sink
 * @param resolution short safe text describing how the state was reached
 */
public record MutationExecutionRecord(
        String tenant,
        String subject,
        String idempotencyKey,
        String requestId,
        String planFingerprint,
        String correlationId,
        MutationExecutionState state,
        int attempt,
        long version,
        Instant startedAt,
        Instant updatedAt,
        Instant leaseExpiresAt,
        ExecutionReceipt receipt,
        List<Integer> affectedRows,
        boolean auditAcknowledged,
        String resolution) {

    public static final String SAFE_ID = "[A-Za-z0-9._:-]{1,128}";

    public MutationExecutionRecord {
        if (idempotencyKey == null || !idempotencyKey.matches(SAFE_ID))
            throw new IllegalArgumentException("idempotencyKey must contain 1-128 safe identifier characters");
        Objects.requireNonNull(requestId, "requestId");
        Objects.requireNonNull(planFingerprint, "planFingerprint");
        Objects.requireNonNull(state, "state");
        Objects.requireNonNull(startedAt, "startedAt");
        Objects.requireNonNull(updatedAt, "updatedAt");
        Objects.requireNonNull(receipt, "receipt");
        if (attempt < 1) throw new IllegalArgumentException("attempt must be >= 1");
        affectedRows = affectedRows == null ? List.of() : List.copyOf(affectedRows);
    }

    /** Stable identity of the idempotency scope + key (unambiguous for null/empty tenant or subject). */
    public String identity() { return identity(tenant, subject, idempotencyKey); }

    public static String identity(String tenant, String subject, String key) {
        return enc(tenant) + "\u001f" + enc(subject) + "\u001f" + key;
    }

    private static String enc(String v) { return v == null ? "\u0000" : "s" + v; }

    public MutationExecutionRecord withVersion(long value) {
        return new MutationExecutionRecord(tenant, subject, idempotencyKey, requestId, planFingerprint,
                correlationId, state, attempt, value, startedAt, updatedAt, leaseExpiresAt, receipt,
                affectedRows, auditAcknowledged, resolution);
    }

    public MutationExecutionRecord transition(MutationExecutionState next, Instant now, ExecutionReceipt nextReceipt,
            List<Integer> rows, String nextResolution) {
        return new MutationExecutionRecord(tenant, subject, idempotencyKey, requestId, planFingerprint,
                correlationId, next, attempt, version, startedAt, now, null, nextReceipt, rows,
                auditAcknowledged, nextResolution);
    }

    public MutationExecutionRecord withAuditAcknowledged(Instant now) {
        return new MutationExecutionRecord(tenant, subject, idempotencyKey, requestId, planFingerprint,
                correlationId, state, attempt, version, startedAt, now, leaseExpiresAt, receipt, affectedRows,
                true, resolution);
    }

    /** A new attempt after an {@code ABORTED} one: new request id, fresh provisional receipt and lease. */
    public MutationExecutionRecord reopen(String newRequestId, String newCorrelationId, Instant now, Instant lease,
            ExecutionReceipt provisional) {
        return new MutationExecutionRecord(tenant, subject, idempotencyKey, newRequestId, planFingerprint,
                newCorrelationId, MutationExecutionState.STARTED, attempt + 1, version, now, now, lease,
                provisional, List.of(), false, null);
    }
}
