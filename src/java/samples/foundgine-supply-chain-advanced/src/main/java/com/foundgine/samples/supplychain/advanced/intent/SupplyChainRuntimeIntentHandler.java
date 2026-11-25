package com.foundgine.samples.supplychain.advanced.intent;

import com.foundgine.core.execution.CancellationToken;
import com.foundgine.core.execution.ExecutionContext;
import com.foundgine.core.execution.ExecutionContextKeys;
import java.util.Map;
import com.foundgine.runtime.FoundgineIntentOutcome;
import com.foundgine.runtime.FoundgineMutationIntentRequest;
import com.foundgine.runtime.IFoundgineMutations;
import com.foundgine.runtime.FoundgineIntentRequest;
import com.foundgine.runtime.FoundgineIntentResult;
import com.foundgine.runtime.IFoundgineIntentHandler;

import java.util.Objects;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.ExecutionException;
import java.util.function.Function;

/** Shared runtime intent boundary used by the Advanced sample's HTTP, MCP and GraphQL adapters. */
public final class SupplyChainRuntimeIntentHandler
        implements IFoundgineIntentHandler<IntentCaller> {
    private final SupplyChainIntentService service;
    private final Function<IntentCaller, IFoundgineMutations> mutationsForCaller;

    public SupplyChainRuntimeIntentHandler(SupplyChainIntentService service) {
        this(service, null);
    }

    /** Creates a unified read/write boundary; mutation engines are resolved from trusted caller context. */
    public SupplyChainRuntimeIntentHandler(
            SupplyChainIntentService service,
            Function<IntentCaller, IFoundgineMutations> mutationsForCaller) {
        this.service = Objects.requireNonNull(service, "service");
        this.mutationsForCaller = mutationsForCaller;
    }

    @Override
    public CompletionStage<FoundgineIntentResult> handleAsync(
            FoundgineIntentRequest request,
            IntentCaller authenticatedCaller,
            CancellationToken cancellationToken) {
        Objects.requireNonNull(request, "request");
        Objects.requireNonNull(cancellationToken, "cancellationToken");
        if (authenticatedCaller == null) {
            return CompletableFuture.completedFuture(FoundgineIntentResult.failure(
                    FoundgineIntentOutcome.UNAUTHENTICATED,
                    "unauthenticated",
                    "An authenticated caller is required."));
        }

        try {
            cancellationToken.throwIfCancellationRequested();
            return switch (request.operation()) {
                case CAPABILITIES -> CompletableFuture.completedFuture(
                        FoundgineIntentResult.success(service.capabilities(authenticatedCaller)));
                case EXPLAIN -> CompletableFuture.completedFuture(
                        FoundgineIntentResult.success(service.explain(authenticatedCaller, request.intent())));
                case EXECUTE -> service.execute(authenticatedCaller, request.intent(), cancellationToken, request.correlationId())
                        .handle((data, error) -> error == null
                                ? FoundgineIntentResult.success(data)
                                : classify(error));
            };
        } catch (Throwable error) {
            return CompletableFuture.completedFuture(classify(error));
        }
    }


    @Override
    public CompletionStage<FoundgineIntentResult> handleMutationAsync(
            FoundgineMutationIntentRequest request,
            IntentCaller authenticatedCaller,
            CancellationToken cancellationToken) {
        Objects.requireNonNull(request, "request");
        Objects.requireNonNull(cancellationToken, "cancellationToken");
        if (authenticatedCaller == null) {
            return CompletableFuture.completedFuture(FoundgineIntentResult.failure(
                    FoundgineIntentOutcome.UNAUTHENTICATED,
                    "unauthenticated",
                    "An authenticated caller is required."));
        }
        if (mutationsForCaller == null) {
            return CompletableFuture.completedFuture(FoundgineIntentResult.failure(
                    FoundgineIntentOutcome.FORBIDDEN,
                    "mutation_not_configured",
                    "Mutations are not enabled for this application boundary."));
        }
        try {
            cancellationToken.throwIfCancellationRequested();
            ExecutionContext executionContext = request.correlationId() == null || request.correlationId().isBlank()
                    ? ExecutionContext.EMPTY
                    : new ExecutionContext(Map.of(ExecutionContextKeys.CORRELATION_ID, request.correlationId()));
            IFoundgineMutations mutations = Objects.requireNonNull(
                    mutationsForCaller.apply(authenticatedCaller), "mutation engine");
            return switch (request.operation()) {
                case DRY_RUN -> CompletableFuture.completedFuture(
                        FoundgineIntentResult.success(mutations.dryRun(request.mutation(), request.correlationId())));
                case APPROVE -> CompletableFuture.completedFuture(
                        FoundgineIntentResult.success(
                                mutations.approve(request.mutation(), authenticatedCaller.tenantId() + ":" + authenticatedCaller.role().name(), request.correlationId())));
                case EXECUTE -> mutations.executeAsync(
                                request.mutation(), executionContext, cancellationToken)
                        .handle((result, error) -> error == null
                                ? FoundgineIntentResult.success(result)
                                : classify(error));
                case EXECUTE_APPROVED -> mutations.executeApprovedAsync(
                                request.approval(), executionContext, cancellationToken)
                        .handle((result, error) -> error == null
                                ? FoundgineIntentResult.success(result)
                                : classify(error));
            };
        } catch (Throwable error) {
            return CompletableFuture.completedFuture(classify(error));
        }
    }

    private static FoundgineIntentResult classify(Throwable error) {
        Throwable cause = error;
        while ((cause instanceof CompletionException || cause instanceof ExecutionException)
                && cause.getCause() != null) {
            cause = cause.getCause();
        }
        var failure = SupplyChainIntentService.failure(cause);
        FoundgineIntentOutcome outcome = switch (failure.status()) {
            case 400 -> FoundgineIntentOutcome.INVALID_INTENT;
            case 403 -> FoundgineIntentOutcome.FORBIDDEN;
            case 504 -> FoundgineIntentOutcome.TIMEOUT;
            default -> FoundgineIntentOutcome.FAILED;
        };
        return FoundgineIntentResult.failure(outcome, failure.type(), failure.message());
    }
}
