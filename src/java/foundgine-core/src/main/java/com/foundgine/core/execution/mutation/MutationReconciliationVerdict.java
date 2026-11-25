package com.foundgine.core.execution.mutation;

import java.util.Objects;

/**
 * A provider's answer about whether a dispatched mutation became durable.
 *
 * <p><b>Fencing contract.</b> {@link Outcome#NOT_APPLIED} must be binding: once returned, the provider must guarantee
 * that a late or still-running execution carrying the same request id can no longer commit (for example by inserting a
 * tombstone row under the same unique key the executing transaction inserts its marker into). Without that guarantee a
 * provider must answer {@link Outcome#UNKNOWN}, because the runtime will allow a retry after "not applied".
 */
public record MutationReconciliationVerdict(Outcome outcome, String evidenceFingerprint, String detail) {
    public enum Outcome { APPLIED, NOT_APPLIED, UNKNOWN }

    public MutationReconciliationVerdict {
        Objects.requireNonNull(outcome, "outcome");
        if (outcome == Outcome.APPLIED && (evidenceFingerprint == null || evidenceFingerprint.isBlank()))
            throw new IllegalArgumentException("An APPLIED verdict requires a non-blank evidence fingerprint.");
    }

    /** The mutation is durable. {@code evidenceFingerprint} fingerprints the provider's proof (not the data). */
    public static MutationReconciliationVerdict applied(String evidenceFingerprint) {
        return new MutationReconciliationVerdict(Outcome.APPLIED, evidenceFingerprint, null);
    }

    /** The mutation is not durable and, per the fencing contract, can no longer become durable. */
    public static MutationReconciliationVerdict notApplied(String detail) {
        return new MutationReconciliationVerdict(Outcome.NOT_APPLIED, null, detail);
    }

    public static MutationReconciliationVerdict unknown(String detail) {
        return new MutationReconciliationVerdict(Outcome.UNKNOWN, null, detail);
    }
}
