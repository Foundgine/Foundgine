using Foundgine.Core.Execution;

namespace Foundgine.Runtime.Recovery;

/// <summary>
/// The provider committed the mutation but the execution ledger could not record the commit. <see cref="ExecutionResult"/>
/// is evidence of the completed operation. The ledger still holds the <c>Started</c> intent, so recovery will resolve it
/// through the provider's reconciler; callers must not blindly retry.
/// </summary>
public sealed class MutationCommittedUnrecordedException : Exception
{
    public MutationCommittedUnrecordedException(MutationExecutionResult executionResult, Exception innerException)
        : base("Mutation completed, but the execution ledger did not record it; reconcile using receipt "
               + executionResult.Receipt?.RequestId, innerException)
    {
        ExecutionResult = executionResult;
    }

    public MutationExecutionResult ExecutionResult { get; }
}
