package com.foundgine.runtime;

import com.foundgine.core.execution.CancellationToken;
import com.foundgine.core.execution.ExecutionContext;

import com.foundgine.runtime.recovery.MutationRecovery;

import java.util.Optional;
import java.util.concurrent.CompletionStage;

/**
 * Port of {@code Foundgine.Runtime.IFoundgineMutations}.
 *
 * <p>C#'s {@code ExecutionContext? context = null, CancellationToken cancellationToken = default}
 * optional parameters are ported as the same overload pair used throughout this port.
 */
public interface IFoundgineMutations {

    MutationDryRunResult dryRun(SemanticMutationRequest request);

    /** Correlated dry-run overload; implementations may use the identifier for audit tracing only. */
    default MutationDryRunResult dryRun(SemanticMutationRequest request, String correlationId) {
        return dryRun(request);
    }

    CompletionStage<MutationExecutionResult> executeAsync(
            SemanticMutationRequest request,
            ExecutionContext context,
            CancellationToken cancellationToken);

    default CompletionStage<MutationExecutionResult> executeAsync(
            SemanticMutationRequest request, ExecutionContext context) {
        return executeAsync(request, context, CancellationToken.NONE);
    }

    default CompletionStage<MutationExecutionResult> executeAsync(SemanticMutationRequest request) {
        return executeAsync(request, null, CancellationToken.NONE);
    }

    MutationPlanApproval approve(SemanticMutationRequest request, String approvedBy);

    /** Correlated approval overload; the caller identity must still come from the trusted principal. */
    default MutationPlanApproval approve(SemanticMutationRequest request, String approvedBy, String correlationId) {
        return approve(request, approvedBy);
    }

    CompletionStage<MutationExecutionResult> executeApprovedAsync(
            MutationPlanApproval approval,
            ExecutionContext context,
            CancellationToken cancellationToken);

    default CompletionStage<MutationExecutionResult> executeApprovedAsync(
            MutationPlanApproval approval, ExecutionContext context) {
        return executeApprovedAsync(approval, context, CancellationToken.NONE);
    }

    default CompletionStage<MutationExecutionResult> executeApprovedAsync(
            MutationPlanApproval approval) {
        return executeApprovedAsync(approval, null, CancellationToken.NONE);
    }

    /** Reconciliation and recovery operations; empty unless the implementation keeps an execution ledger. */
    default Optional<MutationRecovery> recovery() {
        return Optional.empty();
    }
}
