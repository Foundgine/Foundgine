package com.foundgine.core.execution.mutation;

import java.time.Instant;

/**
 * What the runtime knows about an execution whose outcome is unknown. Carries identifiers and fingerprints only, never
 * request or result payloads.
 *
 * @param requestId the engine-assigned execution id, also given to the provider under
 *     {@code ExecutionContextKeys.EXECUTION_REQUEST_ID} when it ran
 * @param tenant tenant of the idempotency scope (may be null)
 * @param subject subject of the idempotency scope (may be null)
 * @param idempotencyKey the idempotency key of the execution
 * @param planFingerprint fingerprint of the authorized plan that was dispatched
 * @param correlationId the correlation id of the original request
 * @param startedAt when the engine recorded the intent to execute
 */
public record MutationReconciliationQuery(
        String requestId,
        String tenant,
        String subject,
        String idempotencyKey,
        String planFingerprint,
        String correlationId,
        Instant startedAt) {}
