package com.foundgine.runtime;

/**
 * Signals that the provider completed a mutation but the configured audit sink did not acknowledge
 * its success event. The attached result is evidence of the completed provider operation. Callers
 * must reconcile by receipt/request ID and must not blindly retry the mutation.
 */
public final class MutationAuditPersistenceException extends RuntimeException {
    private final MutationExecutionResult executionResult;

    public MutationAuditPersistenceException(MutationExecutionResult executionResult, Throwable cause) {
        super("Mutation completed, but its success audit event was not persisted; reconcile using receipt "
                + executionResult.receipt().requestId(), cause);
        this.executionResult = executionResult;
    }

    public MutationExecutionResult executionResult() { return executionResult; }
}
