package com.foundgine.runtime;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.execution.CancellationToken;
import com.foundgine.core.semantic.intent.ReadIntent;
import com.foundgine.core.semantic.mutation.SemanticMutationOperationGraph;
import com.foundgine.core.semantic.mutation.SemanticMutationOperation;
import com.foundgine.core.semantic.mutation.SemanticMutationKind;
import com.foundgine.core.abstractions.EntityId;
import org.junit.jupiter.api.Test;

import java.util.List;
import java.util.concurrent.CompletableFuture;

class FoundgineIntentBoundaryContractTest {

    @Test
    void requestFactoriesEnforceTheOperationIntentShape() {
        var intent = new ReadIntent("Customer", List.of());

        assertEquals(FoundgineIntentOperation.EXECUTE, FoundgineIntentRequest.execute(intent).operation());
        assertSame(intent, FoundgineIntentRequest.explain(intent).intent());
        assertNull(FoundgineIntentRequest.capabilities().intent());
        assertEquals("trace-001", FoundgineIntentRequest.execute(intent, "trace-001").correlationId());
        assertThrows(IllegalArgumentException.class,
                () -> FoundgineIntentRequest.execute(intent, "bad\ntrace"));

        assertThrows(
                IllegalArgumentException.class,
                () -> new FoundgineIntentRequest(FoundgineIntentOperation.EXECUTE, null));
        assertThrows(
                IllegalArgumentException.class,
                () -> new FoundgineIntentRequest(FoundgineIntentOperation.CAPABILITIES, intent));
    }

    @Test
    void intentHandlerIsAProtocolNeutralAsyncFunctionalBoundary() {
        var expected = FoundgineIntentResult.success("accepted");
        IFoundgineIntentHandler<String> handler =
                (request, caller, cancellationToken) ->
                        CompletableFuture.completedFuture(expected);

        var actual =
                handler.handleAsync(
                                FoundgineIntentRequest.capabilities(), null, CancellationToken.NONE)
                        .toCompletableFuture()
                        .join();

        assertSame(expected, actual);
        assertTrue(actual.succeeded());
    }


    @Test
    void mutationIntentFactoriesEnforceLifecyclePayloads() {
        var mutation = new SemanticMutationRequest(new SemanticMutationOperationGraph(List.of(
                new SemanticMutationOperation(EntityId.create("Command"), SemanticMutationKind.CREATE,
                        List.of(), null, List.of(), List.of(), List.of(), List.of()))));
        assertEquals(FoundgineMutationIntentOperation.DRY_RUN,
                FoundgineMutationIntentRequest.dryRun(mutation).operation());
        assertSame(mutation, FoundgineMutationIntentRequest.execute(mutation).mutation());
        assertEquals(FoundgineMutationIntentOperation.APPROVE,
                FoundgineMutationIntentRequest.approve(mutation).operation());
        var approval = new MutationPlanApproval(mutation, "approval", "fingerprint", "trusted-caller", java.time.Instant.now());
        assertSame(approval, FoundgineMutationIntentRequest.executeApproved(approval).approval());
        assertEquals("trace-002", FoundgineMutationIntentRequest.execute(mutation)
                .withCorrelationId("trace-002").correlationId());
        assertThrows(IllegalArgumentException.class,
                () -> new FoundgineMutationIntentRequest(
                        FoundgineMutationIntentOperation.APPROVE, null, null));
        assertThrows(IllegalArgumentException.class,
                () -> new FoundgineMutationIntentRequest(
                        FoundgineMutationIntentOperation.EXECUTE_APPROVED, mutation, null));
    }

    @Test
    void mutationHandlerFailsClosedWhenAnApplicationDoesNotEnableWrites() {
        var mutation = new SemanticMutationRequest(new SemanticMutationOperationGraph(List.of(
                new SemanticMutationOperation(EntityId.create("Command"), SemanticMutationKind.CREATE,
                        List.of(), null, List.of(), List.of(), List.of(), List.of()))));
        IFoundgineIntentHandler<String> handler = (request, caller, cancellationToken) ->
                CompletableFuture.completedFuture(FoundgineIntentResult.success("read"));

        var result = handler.handleMutationAsync(
                        FoundgineMutationIntentRequest.execute(mutation), "trusted-caller")
                .toCompletableFuture().join();

        assertEquals(FoundgineIntentOutcome.FORBIDDEN, result.outcome());
        assertEquals("mutation_not_supported", result.errorCode());

        var unauthenticated = handler.handleMutationAsync(
                        FoundgineMutationIntentRequest.execute(mutation), null)
                .toCompletableFuture().join();
        assertEquals(FoundgineIntentOutcome.UNAUTHENTICATED, unauthenticated.outcome());
    }

    @Test
    void resultFactoryRejectsSuccessAsFailureAndNeverCombinesFailureWithData() {
        assertThrows(
                IllegalArgumentException.class,
                () -> FoundgineIntentResult.failure(FoundgineIntentOutcome.SUCCEEDED, "bad", "bad"));
        assertThrows(
                IllegalArgumentException.class,
                () ->
                        new FoundgineIntentResult(
                                "data", FoundgineIntentOutcome.FORBIDDEN, "forbidden", "Not allowed"));

        var failure =
                FoundgineIntentResult.failure(
                        FoundgineIntentOutcome.INVALID_INTENT, "invalid_intent", "Invalid request");
        assertFalse(failure.succeeded());
        assertNull(failure.data());
    }
}
