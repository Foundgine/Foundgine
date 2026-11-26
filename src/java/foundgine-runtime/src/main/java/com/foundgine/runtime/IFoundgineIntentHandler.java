package com.foundgine.runtime;

import com.foundgine.core.execution.CancellationToken;
import java.util.concurrent.CompletionStage;

/**
 * Application-owned intent boundary consumed by transport adapters.
 *
 * <p>The HTTP, GraphQL and MCP adapters should all call the same implementation. The authenticated
 * caller is supplied by the host using its own principal type, never reconstructed from intent fields
 * or accepted from an untrusted protocol payload. The type parameter keeps the runtime independent
 * of Spring Security, Jakarta Security, servlet APIs and any particular identity provider. Implementations
 * should fail closed when the caller is absent or invalid and should map internal exceptions to safe
 * {@link FoundgineIntentResult} values.
 *
 * <p>This interface deliberately does not prescribe how an application resolves identities,
 * selects tenant policy, explains intent, or shapes result data. It standardizes the boundary so
 * those decisions are shared across protocols rather than duplicated in each adapter.
 */
@FunctionalInterface
public interface IFoundgineIntentHandler<TPrincipal> {

    CompletionStage<FoundgineIntentResult> handleAsync(
            FoundgineIntentRequest request,
            TPrincipal authenticatedCaller,
            CancellationToken cancellationToken);

    default CompletionStage<FoundgineIntentResult> handleAsync(
            FoundgineIntentRequest request, TPrincipal authenticatedCaller) {
        return handleAsync(request, authenticatedCaller, CancellationToken.NONE);
    }

    /**
     * Handles a semantic mutation through the same application-owned boundary as read intents.
     * Implementations that do not expose mutations fail closed by default.
     */
    default CompletionStage<FoundgineIntentResult> handleMutationAsync(
            FoundgineMutationIntentRequest request,
            TPrincipal authenticatedCaller,
            CancellationToken cancellationToken) {
        FoundgineIntentResult failure = authenticatedCaller == null
                ? FoundgineIntentResult.failure(
                        FoundgineIntentOutcome.UNAUTHENTICATED,
                        "unauthenticated",
                        "An authenticated caller is required.")
                : FoundgineIntentResult.failure(
                        FoundgineIntentOutcome.FORBIDDEN,
                        "mutation_not_supported",
                        "This application intent boundary does not expose mutations.");
        return java.util.concurrent.CompletableFuture.completedFuture(failure);
    }

    default CompletionStage<FoundgineIntentResult> handleMutationAsync(
            FoundgineMutationIntentRequest request, TPrincipal authenticatedCaller) {
        return handleMutationAsync(request, authenticatedCaller, CancellationToken.NONE);
    }
}
