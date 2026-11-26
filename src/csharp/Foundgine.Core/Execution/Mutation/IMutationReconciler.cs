namespace Foundgine.Core.Execution.Mutation;

/// <summary>
/// Optional provider capability: decide, from the provider's own durable state, whether a previously dispatched
/// mutation committed. A typical implementation writes <c>(requestId, idempotencyKey)</c> into a marker table in the
/// same transaction as the mutation and looks it up here. Implementations must be idempotent and must honour the
/// fencing contract documented on <see cref="MutationReconciliationVerdict"/>.
/// </summary>
public interface IMutationReconciler
{
    ValueTask<MutationReconciliationVerdict> ReconcileAsync(
        MutationReconciliationQuery query,
        CancellationToken cancellationToken = default);
}
