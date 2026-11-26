using Foundgine.Core.Execution;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Core.Semantic.Mutation;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;
using Foundgine.Core.Semantic.Security.Execution;
using Foundgine.Runtime.Recovery;

namespace Foundgine.Runtime;

public interface IFoundgineMutations
{
    MutationDryRunResult DryRun(SemanticMutationRequest request);

    Task<MutationExecutionResult> ExecuteAsync(
        SemanticMutationRequest request,
        ExecutionContext? context = null,
        CancellationToken cancellationToken = default);

    MutationPlanApproval Approve(SemanticMutationRequest request, string approvedBy);

    Task<MutationExecutionResult> ExecuteApprovedAsync(
        MutationPlanApproval approval,
        ExecutionContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciliation and recovery operations, or <c>null</c> when no execution ledger is configured. A default
    /// member so existing implementations keep compiling.
    /// </summary>
    MutationRecovery? Recovery => null;
}

public sealed record SemanticMutationRequest(
    SemanticMutationOperationGraph Graph,
    SecurityExecutionContext? Security = null);

public sealed record MutationDryRunResult(
    string PlanFingerprint,
    IReadOnlyList<MutationPlanOperation> Operations,
    IReadOnlyList<string> Effects);

public sealed record MutationPlanOperation(
    int Index,
    string Entity,
    string Kind,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> ReturnFields);

public sealed record MutationPlanApproval(
    SemanticMutationRequest Request,
    string ApprovalId,
    string PlanFingerprint,
    string ApprovedBy,
    DateTimeOffset ApprovedAt);

/// <summary>Outcome of a mutation. <c>Receipt</c> is the evidence of the execution; for an idempotent replay it is the receipt of the original execution.</summary>
public sealed record MutationExecutionResult(
    MutationBatchResult Result,
    string PlanFingerprint,
    string ResultFingerprint,
    string? ApprovalId,
    string? ApprovedBy,
    ExecutionReceipt? Receipt = null);
