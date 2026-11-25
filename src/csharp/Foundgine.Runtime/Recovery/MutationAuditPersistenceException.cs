namespace Foundgine.Runtime.Recovery;

/// <summary>
/// The provider completed a mutation but the configured audit sink did not acknowledge its success event.
/// <see cref="ExecutionResult"/> is evidence of the completed provider operation. Callers must reconcile by
/// receipt/request id and must not blindly retry the mutation.
/// </summary>
public sealed class MutationAuditPersistenceException : Exception
{
    public MutationAuditPersistenceException(MutationExecutionResult executionResult, Exception innerException)
        : base("Mutation completed, but its success audit event was not persisted; reconcile using receipt "
               + executionResult.Receipt?.RequestId, innerException)
    {
        ExecutionResult = executionResult;
    }

    public MutationExecutionResult ExecutionResult { get; }
}
