package com.foundgine.core.execution;

import java.time.Duration;
import java.time.Instant;
import java.util.List;

/**
 * Port of {@code Foundgine.Core.Execution.ExecutionReceipt}.
 *
 * <p>Immutable, provider-neutral evidence that a semantic execution occurred. The receipt
 * deliberately contains fingerprints rather than request/result payloads so it can be persisted or
 * transported without copying domain data.
 */
public record ExecutionReceipt(
        String requestId,
        String status,
        String semanticModelVersion,
        int capabilityContractVersion,
        int capabilityVersion,
        int intentVersion,
        int planVersion,
        String intentFingerprint,
        String planFingerprint,
        String authorizationFingerprint,
        String provider,
        Instant startedAt,
        Instant completedAt,
        List<Integer> affectedNodeIds,
        List<String> effects,
        String resultFingerprint,
        String approvalId,
        String approvedBy,
        Instant approvedAt,
        String warrantId,
        String warrantDigest,
        String securityInvariantDigest,
        String correlationId) {

    /** Source-compatible constructor for receipts created before correlation metadata. */
    public ExecutionReceipt(String requestId, String status, String semanticModelVersion,
            int capabilityContractVersion, int capabilityVersion, int intentVersion, int planVersion,
            String intentFingerprint, String planFingerprint, String authorizationFingerprint,
            String provider, Instant startedAt, Instant completedAt, List<Integer> affectedNodeIds,
            List<String> effects, String resultFingerprint, String approvalId, String approvedBy,
            Instant approvedAt, String warrantId, String warrantDigest, String securityInvariantDigest) {
        this(requestId, status, semanticModelVersion, capabilityContractVersion, capabilityVersion,
                intentVersion, planVersion, intentFingerprint, planFingerprint, authorizationFingerprint,
                provider, startedAt, completedAt, affectedNodeIds, effects, resultFingerprint, approvalId,
                approvedBy, approvedAt, warrantId, warrantDigest, securityInvariantDigest, null);
    }

    public ExecutionReceipt {
        if (correlationId != null && !correlationId.matches("[A-Za-z0-9._:-]{1,128}"))
            throw new IllegalArgumentException("correlationId must contain 1-128 safe identifier characters");
    }

    /** Copy with a new status, result fingerprint and completion time (used when an execution is reconciled). */
    public ExecutionReceipt withOutcome(String newStatus, String newResultFingerprint, Instant newCompletedAt) {
        return new ExecutionReceipt(requestId, newStatus, semanticModelVersion, capabilityContractVersion,
                capabilityVersion, intentVersion, planVersion, intentFingerprint, planFingerprint,
                authorizationFingerprint, provider, startedAt, newCompletedAt, affectedNodeIds, effects,
                newResultFingerprint, approvalId, approvedBy, approvedAt, warrantId, warrantDigest,
                securityInvariantDigest, correlationId);
    }

    public long elapsedMilliseconds() {
        long millis = Duration.between(startedAt, completedAt).toMillis();
        return Math.max(0, millis);
    }
}
