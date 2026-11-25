package com.foundgine.samples.supplychain.advanced.mutation;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.foundgine.providers.tools.mcp.FoundgineMcpMutationTools;
import com.foundgine.providers.tools.mcp.FoundgineMcpTools;
import com.foundgine.runtime.MutationExecutionResult;
import com.foundgine.runtime.MutationPlanApproval;
import com.foundgine.runtime.FoundgineMutationIntentRequest;
import com.foundgine.runtime.FoundgineIntentResult;
import com.foundgine.runtime.IFoundgineIntentHandler;
import com.foundgine.runtime.controlplane.auditlog.AuditEvent;
import com.foundgine.runtime.controlplane.auditlog.IAuditLog;
import com.foundgine.runtime.controlplane.auditlog.InMemoryAuditLog;
import com.foundgine.runtime.controlplane.auditlog.HashChainedAuditLog;
import com.foundgine.core.execution.ExecutionEvidenceFactory;
import com.foundgine.samples.supplychain.advanced.intent.IntentCaller;
import com.foundgine.samples.supplychain.advanced.intent.SupplyChainRuntimeIntentHandler;
import com.foundgine.samples.supplychain.advanced.authorization.Authorization;
import com.foundgine.samples.supplychain.advanced.authorization.SupplyChainAuthorization;
import com.foundgine.samples.supplychain.advanced.data.SupplyChainData;
import com.foundgine.samples.supplychain.advanced.intent.SupplyChainIntentService;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Objects;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentMap;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;

/**
 * Application protocol facade for the Advanced sample.
 *
 * <p>HTTP, GraphQL and MCP adapters decode transport payloads into semantic commands and hand them
 * to the same runtime intent handler. No protocol-specific concepts leak into the domain mutation
 * services.
 */
public final class AdvancedMcpFacade {
    private final ObjectMapper mapper = new ObjectMapper();
    private final FoundgineMcpTools queryTools;
    private final FoundgineMcpMutationTools mutationTools;
    private final SupplyChainIntentService intentService;
    private final IFoundgineIntentHandler<IntentCaller> intentHandler;
    private final SupplyChainData data;
    private final Authorization.Context authorizationTemplate;
    private final IAuditLog auditLog = new HashChainedAuditLog(new InMemoryAuditLog());
    private final ConcurrentMap<String, ApprovalEnvelope> approvals = new ConcurrentHashMap<>();
    private static final int MAX_PENDING_APPROVALS = 512;
    private static final java.time.Duration APPROVAL_LIFETIME = java.time.Duration.ofMinutes(10);

    public AdvancedMcpFacade(SupplyChainData data, Authorization.Context auth) {
        this.data = Objects.requireNonNull(data);
        this.authorizationTemplate = Objects.requireNonNull(auth);

        this.intentService = new SupplyChainIntentService(data, auditLog);
        this.intentHandler = new SupplyChainRuntimeIntentHandler(
                intentService,
                caller -> new AdvancedMutationPipeline(
                        this.data,
                        new Authorization.Context(
                                caller.tenantId(),
                                this.authorizationTemplate.allowedWarehouses(),
                                Authorization.Role.valueOf(caller.role().name()),
                                this.authorizationTemplate.readOnly()),
                        this.auditLog));
        this.queryTools = new FoundgineMcpTools(this::query);
        this.mutationTools = new FoundgineMcpMutationTools(this::mutate);
    }

    /**
     * The canonical intent boundary over the same fixture. Unlike {@link #queryTools()}, whose
     * {@code capabilities} query is a fixed response, this runs the caller's intent through a real
     * Foundgine engine: resolution, authorization, planning and execution.
     */
    public SupplyChainIntentService intentService() {
        return intentService;
    }

    /** Shared read/write runtime handler mounted by HTTP, GraphQL and MCP adapters. */
    public IFoundgineIntentHandler<IntentCaller> intentHandler() {
        return intentHandler;
    }

    /** Development-sample audit stream shared by read/write execution and facade-level denials. */
    public IAuditLog auditLog() { return auditLog; }

    /**
     * Parses the sample's command JSON, binds it to the authenticated caller and dispatches every
     * mutation lifecycle operation through the shared runtime intent handler.
     */
    public CompletionStage<FoundgineIntentResult> dispatchMutation(String json, IntentCaller caller) {
        return dispatchMutation(json, caller, null);
    }

    public CompletionStage<FoundgineIntentResult> dispatchMutation(String json, IntentCaller caller, String correlationId) {
        return dispatchMutation(json, caller, correlationId, false);
    }

    /**
     * @param trustedActor true only for the host-configured MCP tool, where the host (not a request
     *     header) fixed the caller; a body-supplied {@code actor} then names the end user acting for
     *     that caller. For request-authenticated callers the actor is always derived from the caller.
     */
    private CompletionStage<FoundgineIntentResult> dispatchMutation(
            String json, IntentCaller caller, String correlationId, boolean trustedActor) {
        try {
            Objects.requireNonNull(caller, "caller");
            JsonNode request = mapper.readTree(json);
            String mode = request.path("mode").asText("execute").toLowerCase(java.util.Locale.ROOT);
            if ("execute_approved".equals(mode)) {
                rejectUnknownProperties(request, java.util.Set.of("mode", "approvalId"));
                String handle = requiredText(request, "approvalId");
                ApprovalEnvelope envelope = approvals.get(handle);
                if (envelope == null || envelope.expiresAt().isBefore(Instant.now())) {
                    if (envelope != null) approvals.remove(handle, envelope);
                    recordBoundaryDenied(caller, correlationId, handle, "approval-invalid");
                    return CompletableFuture.completedFuture(FoundgineIntentResult.failure(
                            com.foundgine.runtime.FoundgineIntentOutcome.INVALID_INTENT,
                            "approval_invalid", "The approval is missing, expired or already used."));
                }
                if (!envelope.tenantId().equals(caller.tenantId()) || envelope.role() != caller.role()) {
                    recordBoundaryDenied(caller, correlationId, handle, "approval-caller-mismatch");
                    return CompletableFuture.completedFuture(FoundgineIntentResult.failure(
                            com.foundgine.runtime.FoundgineIntentOutcome.FORBIDDEN,
                            "approval_caller_mismatch", "The approval belongs to a different caller context."));
                }
                if (!approvals.remove(handle, envelope)) {
                    recordBoundaryDenied(caller, correlationId, handle, "approval-replay");
                    return CompletableFuture.completedFuture(FoundgineIntentResult.failure(
                            com.foundgine.runtime.FoundgineIntentOutcome.INVALID_INTENT,
                            "approval_invalid", "The approval is missing, expired or already used."));
                }
                return intentHandler.handleMutationAsync(
                        FoundgineMutationIntentRequest.executeApproved(envelope.approval()).withCorrelationId(correlationId), caller)
                        .thenApply(this::transportResult);
            }

            rejectUnknownProperties(request, java.util.Set.of(
                    "tool", "mode", "actor", "customerId", "productId", "quantity", "orderId", "idempotencyKey"));
            String tool = requiredText(request, "tool");
            String actor = trustedActor && request.hasNonNull("actor")
                    ? requiredText(request, "actor")
                    : caller.tenantId() + ":" + caller.role().name();
            AdvancedMutationPipeline callerPipeline = new AdvancedMutationPipeline(
                    data,
                    new Authorization.Context(
                            caller.tenantId(), authorizationTemplate.allowedWarehouses(),
                            Authorization.Role.valueOf(caller.role().name()), authorizationTemplate.readOnly()));
            var mutation = switch (tool) {
                case "place_order" -> callerPipeline.createPlaceOrderRequest(
                        actor, requiredInt(request, "customerId"), requiredInt(request, "productId"),
                        requiredInt(request, "quantity"), requiredText(request, "idempotencyKey"));
                case "cancel_order" -> callerPipeline.createCancelOrderRequest(
                        actor, requiredInt(request, "orderId"), requiredText(request, "idempotencyKey"));
                default -> throw new IllegalArgumentException("Unknown Advanced MCP mutation tool: " + tool);
            };

            FoundgineMutationIntentRequest command = switch (mode) {
                case "execute" -> FoundgineMutationIntentRequest.execute(mutation).withCorrelationId(correlationId);
                case "dry_run" -> FoundgineMutationIntentRequest.dryRun(mutation).withCorrelationId(correlationId);
                case "approve" -> FoundgineMutationIntentRequest.approve(mutation).withCorrelationId(correlationId);
                default -> throw new IllegalArgumentException(
                        "Unknown mutation mode. Use execute, dry_run, approve or execute_approved.");
            };
            return intentHandler.handleMutationAsync(command, caller).thenApply(result -> {
                if (!result.succeeded() || command.operation() != com.foundgine.runtime.FoundgineMutationIntentOperation.APPROVE) {
                    return transportResult(result);
                }
                MutationPlanApproval approval = (MutationPlanApproval) result.data();
                synchronized (approvals) {
                    Instant now = Instant.now();
                    approvals.entrySet().removeIf(entry -> entry.getValue().expiresAt().isBefore(now));
                    if (approvals.size() >= MAX_PENDING_APPROVALS) {
                        recordBoundaryDenied(caller, correlationId, approval.approvalId(), "approval-capacity");
                        return FoundgineIntentResult.failure(
                                com.foundgine.runtime.FoundgineIntentOutcome.FAILED,
                                "approval_capacity", "The pending approval store is full; retry later.");
                    }
                    // The engine approval id is a 128-bit random value; using it as the handle keeps the id the
                    // client holds identical to the one in the execution receipt and audit trail.
                    String handle = approval.approvalId();
                    Instant expiresAt = now.plus(APPROVAL_LIFETIME);
                    approvals.put(handle, new ApprovalEnvelope(approval, caller.tenantId(), caller.role(), expiresAt));
                    return FoundgineIntentResult.success(Map.of(
                            "approvalId", handle,
                            "planFingerprint", approval.planFingerprint(),
                            "approvedBy", approval.approvedBy(),
                            "expiresAt", expiresAt.toString()));
                }
            });
        } catch (Exception e) {
            if (caller != null) recordBoundaryDenied(caller, correlationId, e.getClass().getName(), "mutation-request-invalid");
            return CompletableFuture.failedFuture(e);
        }
    }

    /**
     * Executed mutations carry engine records (receipt instants, field-id keyed rows) that are not a
     * wire format. Every transport (HTTP, GraphQL, MCP) receives the same plain, serializable shape.
     */
    private FoundgineIntentResult transportResult(FoundgineIntentResult result) {
        if (result.succeeded() && result.data() instanceof MutationExecutionResult execution) {
            return FoundgineIntentResult.success(toResponse(execution));
        }
        return result;
    }

    private void recordBoundaryDenied(IntentCaller caller, String correlationId, String subject, String reason) {
        String trace = correlationId != null && correlationId.matches("[A-Za-z0-9._:-]{1,128}")
                ? correlationId : UUID.randomUUID().toString();
        auditLog.record(AuditEvent.createCorrelated(
                AuditEvent.AuditCategory.MUTATION_DENIED, "semantic_mutation_boundary",
                caller == null ? null : caller.tenantId() + ":" + caller.role().name(),
                caller == null ? null : caller.tenantId(),
                ExecutionEvidenceFactory.hash(String.valueOf(subject)),
                "Mutation boundary denied request: " + reason, trace));
    }

    private record ApprovalEnvelope(
            MutationPlanApproval approval, String tenantId,
            SupplyChainAuthorization.Role role, Instant expiresAt) {}

    /** MCP convenience wrapper preserving the existing tool response shape. */
    public CompletionStage<Object> mutate(String json, IntentCaller caller) {
        return mutate(json, caller, null);
    }

    public CompletionStage<Object> mutate(String json, IntentCaller caller, String correlationId) {
        return dispatchMutation(json, caller, correlationId).thenApply(result -> {
            if (!result.succeeded()) {
                throw new java.util.concurrent.CompletionException(
                        new SecurityException(result.errorCode() + ": " + result.errorMessage()));
            }
            return result.data() instanceof MutationExecutionResult execution
                    ? toResponse(execution)
                    : result.data();
        });
    }

    public FoundgineMcpTools queryTools() {
        return queryTools;
    }

    public FoundgineMcpMutationTools mutationTools() {
        return mutationTools;
    }

    private CompletionStage<Object> query(String json) {
        try {
            JsonNode request = mapper.readTree(json);
            String tool = requiredText(request, "tool");

            if ("capabilities".equals(tool)) {
                return CompletableFuture.completedFuture(
                        Map.of(
                                "tools",
                                        new String[] {
                                            "place_order", "cancel_order", "capabilities"
                                        },
                                "mutationBoundary", "Foundgine.Runtime",
                                "authorization", "Foundgine semantic authorization"));
            }

            throw new IllegalArgumentException("Unknown Advanced MCP query tool: " + tool);
        } catch (Exception e) {
            return CompletableFuture.failedFuture(e);
        }
    }

    private CompletionStage<Object> mutate(String json) {
        IntentCaller configuredCaller = new IntentCaller(
                authorizationTemplate.tenantId(),
                SupplyChainAuthorization.Role.valueOf(authorizationTemplate.role().name()));
        return dispatchMutation(json, configuredCaller, null, true).thenApply(result -> {
            if (!result.succeeded()) {
                throw new java.util.concurrent.CompletionException(
                        new SecurityException(result.errorCode() + ": " + result.errorMessage()));
            }
            return result.data() instanceof MutationExecutionResult execution
                    ? toResponse(execution)
                    : result.data();
        });
    }

    private Object toResponse(MutationExecutionResult result) {
        var response = new LinkedHashMap<String, Object>();

        response.put("planFingerprint", result.planFingerprint());
        response.put("resultFingerprint", result.resultFingerprint());
        response.put("approvalId", result.approvalId());
        response.put("approvedBy", result.approvedBy());
        response.put("receipt", mutationReceipt(result.receipt()));
        response.put("results", result.result().results());

        return response;
    }

    private static Map<String, Object> mutationReceipt(
            com.foundgine.core.execution.ExecutionReceipt receipt) {
        if (receipt == null) return null;
        var map = new LinkedHashMap<String, Object>();
        map.put("requestId", receipt.requestId());
        map.put("correlationId", receipt.correlationId());
        map.put("status", receipt.status());
        map.put("semanticModelVersion", receipt.semanticModelVersion());
        map.put("capabilityContractVersion", receipt.capabilityContractVersion());
        map.put("capabilityVersion", receipt.capabilityVersion());
        map.put("intentVersion", receipt.intentVersion());
        map.put("planVersion", receipt.planVersion());
        map.put("intentFingerprint", receipt.intentFingerprint());
        map.put("planFingerprint", receipt.planFingerprint());
        map.put("authorizationFingerprint", receipt.authorizationFingerprint());
        map.put("provider", receipt.provider());
        map.put("startedAt", String.valueOf(receipt.startedAt()));
        map.put("completedAt", String.valueOf(receipt.completedAt()));
        map.put("elapsedMilliseconds", receipt.elapsedMilliseconds());
        map.put("affectedNodeIds", receipt.affectedNodeIds());
        map.put("effects", receipt.effects());
        map.put("resultFingerprint", receipt.resultFingerprint());
        map.put("approvalId", receipt.approvalId());
        map.put("approvedBy", receipt.approvedBy());
        map.put("approvedAt", receipt.approvedAt() == null ? null : receipt.approvedAt().toString());
        map.put("warrantId", receipt.warrantId());
        map.put("warrantDigest", receipt.warrantDigest());
        map.put("securityInvariantDigest", receipt.securityInvariantDigest());
        return map;
    }

    private static void rejectUnknownProperties(JsonNode request, java.util.Set<String> allowed) {
        if (request == null || !request.isObject()) {
            throw new IllegalArgumentException("A mutation command must be a JSON object.");
        }
        var names = request.fieldNames();
        while (names.hasNext()) {
            String name = names.next();
            if (!allowed.contains(name)) {
                throw new IllegalArgumentException("Unknown mutation property: " + name);
            }
        }
    }

    private static String requiredText(JsonNode node, String name) {
        JsonNode value = node.get(name);

        if (value == null || !value.isTextual() || value.asText().isBlank()) {
            throw new IllegalArgumentException("Missing required text field: " + name);
        }

        return value.asText();
    }

    private static int requiredInt(JsonNode node, String name) {
        JsonNode value = node.get(name);

        if (value == null || !value.isIntegralNumber()) {
            throw new IllegalArgumentException("Missing required integer field: " + name);
        }

        if (!value.canConvertToInt()) {
            throw new IllegalArgumentException(
                    "Integer field is outside the supported range: " + name);
        }

        return value.intValue();
    }
}
