package com.foundgine.runtime;

import java.util.Objects;

/**
 * Protocol-neutral semantic mutation command for the application-owned intent boundary.
 *
 * <p>Approver identity is deliberately absent: the handler derives it from the authenticated
 * principal. Approval objects are trusted runtime artifacts. Transport adapters must never
 * deserialize {@link MutationPlanApproval} directly from caller-controlled JSON; expose opaque
 * approval handles backed by server-side storage instead.
 */
public record FoundgineMutationIntentRequest(
        FoundgineMutationIntentOperation operation,
        SemanticMutationRequest mutation,
        MutationPlanApproval approval,
        String correlationId) {

    public FoundgineMutationIntentRequest(FoundgineMutationIntentOperation operation,
            SemanticMutationRequest mutation, MutationPlanApproval approval) {
        this(operation, mutation, approval, null);
    }

    public FoundgineMutationIntentRequest {
        Objects.requireNonNull(operation, "operation");
        if (correlationId != null && !correlationId.matches("[A-Za-z0-9._:-]{1,128}"))
            throw new IllegalArgumentException("correlationId must contain 1-128 safe identifier characters");
        switch (operation) {
            case DRY_RUN, EXECUTE, APPROVE -> {
                if (mutation == null)
                    throw new IllegalArgumentException("this operation requires a mutation");
                if (approval != null)
                    throw new IllegalArgumentException("this operation does not accept an existing approval");
            }
            case EXECUTE_APPROVED -> {
                if (approval == null)
                    throw new IllegalArgumentException("execute-approved requires a trusted approval");
                if (mutation != null)
                    throw new IllegalArgumentException("execute-approved accepts only a trusted approval");
            }
        }
    }

    public static FoundgineMutationIntentRequest dryRun(SemanticMutationRequest mutation) {
        return new FoundgineMutationIntentRequest(FoundgineMutationIntentOperation.DRY_RUN, mutation, null);
    }

    public static FoundgineMutationIntentRequest execute(SemanticMutationRequest mutation) {
        return new FoundgineMutationIntentRequest(FoundgineMutationIntentOperation.EXECUTE, mutation, null);
    }

    public static FoundgineMutationIntentRequest approve(SemanticMutationRequest mutation) {
        return new FoundgineMutationIntentRequest(FoundgineMutationIntentOperation.APPROVE, mutation, null);
    }

    public static FoundgineMutationIntentRequest executeApproved(MutationPlanApproval approval) {
        return new FoundgineMutationIntentRequest(FoundgineMutationIntentOperation.EXECUTE_APPROVED, null, approval);
    }

    public FoundgineMutationIntentRequest withCorrelationId(String value) {
        return new FoundgineMutationIntentRequest(operation, mutation, approval, value);
    }
}
