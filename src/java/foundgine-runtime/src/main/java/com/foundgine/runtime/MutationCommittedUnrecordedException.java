package com.foundgine.runtime;

/**
 * The provider committed the mutation but the execution ledger could not record the commit. The attached result is
 * evidence of the completed operation. The ledger still holds the {@code STARTED} intent, so recovery will resolve it
 * through the provider's reconciler; callers must not blindly retry.
 */
public final class MutationCommittedUnrecordedException extends RuntimeException {
    private final MutationExecutionResult executionResult;

    public MutationCommittedUnrecordedException(MutationExecutionResult executionResult, Throwable cause) {
        super("Mutation completed, but the execution ledger did not record it; reconcile using receipt "
                + executionResult.receipt().requestId(), cause);
        this.executionResult = executionResult;
    }

    public MutationExecutionResult executionResult() { return executionResult; }
}
