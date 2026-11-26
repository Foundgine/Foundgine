namespace Foundgine.Runtime;

/// <summary>Lifecycle step requested against an application's mutation boundary.</summary>
public enum FoundgineMutationIntentOperation
{
    DryRun,
    Execute,
    Approve,
    ExecuteApproved
}

/// <summary>
/// Protocol-neutral semantic mutation command for the application-owned intent boundary.
/// Approver identity is deliberately absent: the handler derives it from the authenticated principal.
/// Approval objects are trusted runtime artifacts. Transport adapters must never deserialize a
/// <see cref="MutationPlanApproval"/> directly from caller-controlled JSON; expose opaque approval
/// handles backed by server-side storage instead.
/// </summary>
public sealed record FoundgineMutationIntentRequest
{
    public FoundgineMutationIntentRequest(
        FoundgineMutationIntentOperation operation,
        SemanticMutationRequest? mutation,
        MutationPlanApproval? approval,
        string? correlationId = null)
    {
        FoundgineCorrelation.Validate(correlationId);
        switch (operation)
        {
            case FoundgineMutationIntentOperation.DryRun:
            case FoundgineMutationIntentOperation.Execute:
            case FoundgineMutationIntentOperation.Approve:
                if (mutation is null)
                    throw new ArgumentException("this operation requires a mutation", nameof(mutation));
                if (approval is not null)
                    throw new ArgumentException("this operation does not accept an existing approval", nameof(approval));
                break;
            case FoundgineMutationIntentOperation.ExecuteApproved:
                if (approval is null)
                    throw new ArgumentException("execute-approved requires a trusted approval", nameof(approval));
                if (mutation is not null)
                    throw new ArgumentException("execute-approved accepts only a trusted approval", nameof(mutation));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Operation = operation;
        Mutation = mutation;
        Approval = approval;
        CorrelationId = correlationId;
    }

    public FoundgineMutationIntentOperation Operation { get; }

    public SemanticMutationRequest? Mutation { get; }

    public MutationPlanApproval? Approval { get; }

    public string? CorrelationId { get; }

    public static FoundgineMutationIntentRequest DryRun(SemanticMutationRequest mutation) =>
        new(FoundgineMutationIntentOperation.DryRun, mutation, null);

    public static FoundgineMutationIntentRequest Execute(SemanticMutationRequest mutation) =>
        new(FoundgineMutationIntentOperation.Execute, mutation, null);

    public static FoundgineMutationIntentRequest Approve(SemanticMutationRequest mutation) =>
        new(FoundgineMutationIntentOperation.Approve, mutation, null);

    public static FoundgineMutationIntentRequest ExecuteApproved(MutationPlanApproval approval) =>
        new(FoundgineMutationIntentOperation.ExecuteApproved, null, approval);

    public FoundgineMutationIntentRequest WithCorrelationId(string? value) =>
        new(Operation, Mutation, Approval, value);
}
