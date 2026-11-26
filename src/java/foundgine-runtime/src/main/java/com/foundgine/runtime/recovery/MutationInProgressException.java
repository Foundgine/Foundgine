package com.foundgine.runtime.recovery;

/** Another execution under the same idempotency key is still within its lease. Retry later. */
public final class MutationInProgressException extends RuntimeException {
    private final String requestId, idempotencyKey;

    public MutationInProgressException(String requestId, String idempotencyKey) {
        super("A mutation with this idempotency key is already in progress (requestId=" + requestId + ").");
        this.requestId = requestId;
        this.idempotencyKey = idempotencyKey;
    }

    public String requestId() { return requestId; }
    public String idempotencyKey() { return idempotencyKey; }
}
