package com.foundgine.runtime.recovery;

/**
 * The mutation was dispatched but whether it became durable is unknown (provider failure other than
 * {@code MutationNotAppliedException}, or an earlier attempt whose lease expired). The same idempotency key must not be
 * executed again until it has been reconciled; see {@link MutationRecovery}.
 */
public final class MutationInDoubtException extends RuntimeException {
    private final String requestId, idempotencyKey;

    public MutationInDoubtException(String requestId, String idempotencyKey, Throwable cause) {
        super("The mutation outcome is unknown; reconcile before retrying (requestId=" + requestId
                + ", idempotencyKey=" + idempotencyKey + ").", cause);
        this.requestId = requestId;
        this.idempotencyKey = idempotencyKey;
    }

    public String requestId() { return requestId; }
    public String idempotencyKey() { return idempotencyKey; }
}
