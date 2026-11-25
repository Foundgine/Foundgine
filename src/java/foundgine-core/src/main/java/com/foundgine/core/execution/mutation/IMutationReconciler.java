package com.foundgine.core.execution.mutation;

/**
 * Optional provider capability: decide, from the provider's own durable state, whether a previously dispatched mutation
 * committed. A typical implementation writes {@code (requestId, idempotencyKey)} into a marker table in the same
 * transaction as the mutation and looks it up here. Implementations must be idempotent and must honour the fencing
 * contract documented on {@link MutationReconciliationVerdict}.
 */
@FunctionalInterface
public interface IMutationReconciler {
    MutationReconciliationVerdict reconcile(MutationReconciliationQuery query);
}
