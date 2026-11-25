package com.foundgine.samples.supplychain.advanced.mcp;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.foundgine.samples.supplychain.advanced.intent.IntentCaller;
import com.foundgine.samples.supplychain.advanced.intent.SupplyChainIntentService;
import com.foundgine.samples.supplychain.advanced.intent.SupplyChainRuntimeIntentHandler;
import com.foundgine.runtime.FoundgineIntentOutcome;
import com.foundgine.runtime.FoundgineIntentRequest;
import com.foundgine.runtime.FoundgineIntentResult;
import com.foundgine.runtime.IFoundgineIntentHandler;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Objects;
import java.util.function.BiFunction;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;

/**
 * HTTP and MCP transport surface for the canonical intent boundary ({@link SupplyChainIntentService}).
 *
 * <p>The body of {@code POST /intent} is the canonical read intent itself (see
 * {@code JsonReadIntentAdapter}). {@code POST /intent/mutation} accepts the sample's typed command
 * JSON and dispatches it through the same runtime handler. The JSON example below is a read request:
 *
 * <pre>{@code
 * {"rootEntity":"Supplier",
 *  "selections":[{"field":"Name"},{"field":"RiskScore"}],
 *  "filter":{"kind":"field","field":"Country","operator":"Eq","value":"NZ"}}
 * }</pre>
 *
 * <ul>
 *   <li>{@code POST /intent}: execute a read intent as the caller
 *   <li>{@code POST /intent/explain}: how it resolves and what the caller's policy leaves; nothing executes
 *   <li>{@code GET /intent/capabilities}: what the caller may read, by entity, field, relationship
 *   <li>{@code POST /intent/mutation}: execute, dry-run, approve or execute-approved a semantic command
 * </ul>
 *
 * <p>The caller (tenant and role) comes from the {@code X-Foundgine-Tenant} and {@code
 * X-Foundgine-Role} headers and never from the body. DEMO ONLY: a real host derives identity from
 * an authenticated principal; see {@link IntentCaller}.
 *
 * <p>MCP: the {@code foundgine_intent} tool takes {@code {"intent": {...}, "mode":
 * "execute|explain|capabilities"}} and uses the same runtime handler, with the caller taken from the same
 * headers of the {@code /mcp} request.
 */
public final class AdvancedIntentEndpoint {
    public static final String PATH = "/intent";
    public static final int MAX_BODY_BYTES = 65_536;

    private static final ObjectMapper JSON = new ObjectMapper();

    private final SupplyChainIntentService service;
    private final IFoundgineIntentHandler<IntentCaller> handler;
    @FunctionalInterface
    public interface MutationDispatcher {
        CompletionStage<FoundgineIntentResult> dispatch(String json, IntentCaller caller, String correlationId);
    }

    private final MutationDispatcher mutationDispatcher;

    public AdvancedIntentEndpoint(SupplyChainIntentService service) {
        this(service, new SupplyChainRuntimeIntentHandler(service), (MutationDispatcher) null);
    }

    public AdvancedIntentEndpoint(
            SupplyChainIntentService service, IFoundgineIntentHandler<IntentCaller> handler) {
        this(service, handler, (MutationDispatcher) null);
    }

    /** Source-compatible adapter for dispatchers that do not consume correlation metadata. */
    public AdvancedIntentEndpoint(
            SupplyChainIntentService service,
            IFoundgineIntentHandler<IntentCaller> handler,
            BiFunction<String, IntentCaller, CompletionStage<FoundgineIntentResult>> mutationDispatcher) {
        this(service, handler, mutationDispatcher == null ? (MutationDispatcher) null
                : (json, caller, correlationId) -> mutationDispatcher.apply(json, caller));
    }

    public AdvancedIntentEndpoint(
            SupplyChainIntentService service,
            IFoundgineIntentHandler<IntentCaller> handler,
            MutationDispatcher mutationDispatcher) {
        this.service = Objects.requireNonNull(service, "service");
        this.handler = Objects.requireNonNull(handler, "handler");
        this.mutationDispatcher = mutationDispatcher;
    }

    /** Shared protocol-neutral handler mounted by the HTTP, MCP and GraphQL surfaces. */
    public IFoundgineIntentHandler<IntentCaller> handler() {
        return handler;
    }

    public java.util.Set<String> knownTenants() {
        return service.knownTenants();
    }

    public void register(HttpServer server) {
        server.createContext(PATH, this::handle);
    }

    // ------------------------------------------------------------------ HTTP

    private void handle(HttpExchange exchange) throws IOException {
        try (exchange) {
            String path = exchange.getRequestURI().getPath();
            String method = exchange.getRequestMethod();
            try {
                switch (path) {
                    case PATH -> {
                        if (!requireMethod(exchange, method, "POST")) return;
                        var caller = caller(exchange);
                        var result = invoke(caller, "execute", readBody(exchange), correlationId(exchange));
                        respond(exchange, status(result), resultBody(result));
                    }
                    case PATH + "/mutation" -> {
                        if (!requireMethod(exchange, method, "POST")) return;
                        if (mutationDispatcher == null) {
                            respondError(exchange, 404, "mutation_not_configured", "Mutations are not configured on this endpoint.");
                            return;
                        }
                        var result = mutationDispatcher.dispatch(readBody(exchange), caller(exchange), correlationId(exchange))
                                .toCompletableFuture().join();
                        respond(exchange, status(result), resultBody(result));
                    }
                    case PATH + "/explain" -> {
                        if (!requireMethod(exchange, method, "POST")) return;
                        var caller = caller(exchange);
                        var result = invoke(caller, "explain", readBody(exchange), correlationId(exchange));
                        respond(exchange, status(result), resultBody(result));
                    }
                    case PATH + "/capabilities" -> {
                        if (!requireMethod(exchange, method, "GET")) return;
                        var result = invoke(caller(exchange), "capabilities", null, correlationId(exchange));
                        respond(exchange, status(result), resultBody(result));
                    }
                    default -> respondError(exchange, 404, "not_found", "Unknown intent path.");
                }
            } catch (PayloadTooLarge e) {
                respondError(exchange, 413, "payload_too_large", e.getMessage());
            } catch (Throwable t) {
                var failure = SupplyChainIntentService.failure(t);
                respondError(exchange, failure.status(), failure.type(), failure.message());
            }
        }
    }

    private static boolean requireMethod(HttpExchange exchange, String actual, String expected)
            throws IOException {
        if (expected.equalsIgnoreCase(actual)) return true;
        exchange.getResponseHeaders().add("Allow", expected);
        respondError(exchange, 405, "method_not_allowed", "Use " + expected + " for this path.");
        return false;
    }

    /** The caller for a request, from headers only. */
    public IntentCaller caller(HttpExchange exchange) {
        var headers = exchange.getRequestHeaders();
        return IntentCaller.resolve(
                headers.getFirst(IntentCaller.TENANT_HEADER),
                headers.getFirst(IntentCaller.ROLE_HEADER),
                service.knownTenants());
    }

    private static String readBody(HttpExchange exchange) throws IOException {
        byte[] bytes = exchange.getRequestBody().readNBytes(MAX_BODY_BYTES + 1);
        if (bytes.length > MAX_BODY_BYTES) {
            throw new PayloadTooLarge("An intent may not exceed " + MAX_BODY_BYTES + " bytes.");
        }
        return new String(bytes, StandardCharsets.UTF_8);
    }

    private static final class PayloadTooLarge extends RuntimeException {
        PayloadTooLarge(String message) {
            super(message);
        }
    }

    private static void respond(HttpExchange exchange, int status, Object body) throws IOException {
        write(exchange, status, JSON.writeValueAsBytes(body));
    }

    private static void respondError(HttpExchange exchange, int status, String type, String message)
            throws IOException {
        var error = new LinkedHashMap<String, Object>();
        error.put("type", type);
        error.put("message", message);
        respond(exchange, status, Map.of("error", error));
    }

    private static void write(HttpExchange exchange, int status, byte[] bytes) throws IOException {
        exchange.getResponseHeaders().set("Content-Type", "application/json");
        exchange.sendResponseHeaders(status, bytes.length);
        try (OutputStream os = exchange.getResponseBody()) {
            os.write(bytes);
        }
    }

    // ------------------------------------------------------------------ MCP

    /**
     * Handles the {@code foundgine_intent} MCP tool. Completes with the JSON text of the result, or
     * fails with the underlying exception (classify it with {@link
     * SupplyChainIntentService#failure}).
     */
    public CompletionStage<String> callTool(IntentCaller caller, JsonNode arguments) {
        return callTool(caller, arguments, null);
    }

    public CompletionStage<String> callTool(IntentCaller caller, JsonNode arguments, String correlationId) {
        try {
            String mode = arguments.path("mode").asText("execute");
            JsonNode intent = arguments.get("intent");
            String intentJson = "capabilities".equals(mode) ? null : intentText(intent);
            FoundgineIntentResult result = invoke(caller, mode, intentJson, correlationId);
            if (!result.succeeded()) {
                RuntimeException failure = switch (result.outcome()) {
                    case FORBIDDEN -> new SecurityException(result.errorMessage());
                    case INVALID_INTENT -> new IllegalArgumentException(result.errorMessage());
                    case TIMEOUT -> new IllegalStateException("deadline: " + result.errorMessage());
                    case UNAUTHENTICATED -> new SecurityException(result.errorMessage());
                    case FAILED -> new IllegalStateException(result.errorMessage());
                    case SUCCEEDED -> new IllegalStateException("Unexpected successful result.");
                };
                return CompletableFuture.failedFuture(failure);
            }
            return CompletableFuture.completedFuture(toJson(result.data()));
        } catch (Throwable t) {
            return CompletableFuture.failedFuture(t);
        }
    }

    /** Dispatches a protocol-neutral mutation command through the same runtime handler. */
    public FoundgineIntentResult invokeMutation(IntentCaller caller, String mutationJson) {
        return invokeMutation(caller, mutationJson, null);
    }

    public FoundgineIntentResult invokeMutation(IntentCaller caller, String mutationJson, String correlationId) {
        if (mutationDispatcher == null) {
            return FoundgineIntentResult.failure(FoundgineIntentOutcome.FORBIDDEN,
                    "mutation_not_configured", "Mutations are not configured on this endpoint.");
        }
        try {
            return mutationDispatcher.dispatch(mutationJson, caller, correlationId).toCompletableFuture().join();
        } catch (Throwable error) {
            var failure = SupplyChainIntentService.failure(error);
            var outcome = switch (failure.status()) {
                case 400 -> FoundgineIntentOutcome.INVALID_INTENT;
                case 403 -> FoundgineIntentOutcome.FORBIDDEN;
                case 504 -> FoundgineIntentOutcome.TIMEOUT;
                default -> FoundgineIntentOutcome.FAILED;
            };
            return FoundgineIntentResult.failure(outcome, failure.type(), failure.message());
        }
    }

    /** Converts the protocol's mode and JSON payload into the shared runtime request. */
    public FoundgineIntentResult invoke(IntentCaller caller, String mode, String intentJson) {
        return invoke(caller, mode, intentJson, null);
    }

    public FoundgineIntentResult invoke(IntentCaller caller, String mode, String intentJson, String correlationId) {
        try {
            if (mode == null || mode.isBlank()) {
                throw new IllegalArgumentException("An intent mode is required.");
            }
            if (!"capabilities".equals(mode) && (intentJson == null || intentJson.isBlank())) {
                throw new IllegalArgumentException("An intent payload is required for execute and explain.");
            }
            FoundgineIntentRequest request = switch (mode) {
                case "capabilities" -> FoundgineIntentRequest.capabilities();
                case "explain" -> FoundgineIntentRequest.explain(service.parseIntent(intentJson));
                case "execute" -> FoundgineIntentRequest.execute(service.parseIntent(intentJson), correlationId);
                default -> throw new IllegalArgumentException(
                        "Unknown mode '" + mode + "'. Use execute, explain or capabilities.");
            };
            return handler.handleAsync(request, caller).toCompletableFuture().join();
        } catch (Throwable error) {
            var failure = SupplyChainIntentService.failure(error);
            var outcome = switch (failure.status()) {
                case 400 -> FoundgineIntentOutcome.INVALID_INTENT;
                case 403 -> FoundgineIntentOutcome.FORBIDDEN;
                case 504 -> FoundgineIntentOutcome.TIMEOUT;
                default -> FoundgineIntentOutcome.FAILED;
            };
            return FoundgineIntentResult.failure(outcome, failure.type(), failure.message());
        }
    }

    private static String correlationId(HttpExchange exchange) {
        String supplied = exchange.getRequestHeaders().getFirst("X-Correlation-ID");
        if (supplied == null || supplied.isBlank()) return java.util.UUID.randomUUID().toString();
        if (supplied.length() > 128 || !supplied.matches("[A-Za-z0-9._:-]+"))
            throw new IllegalArgumentException("X-Correlation-ID must be 1-128 safe identifier characters.");
        return supplied;
    }

    private static int status(FoundgineIntentResult result) {
        if (result.succeeded()) return 200;
        return switch (result.outcome()) {
            case UNAUTHENTICATED -> 401;
            case FORBIDDEN -> 403;
            case INVALID_INTENT -> 400;
            case TIMEOUT -> 504;
            case FAILED -> 500;
            case SUCCEEDED -> 200;
        };
    }

    private static Object resultBody(FoundgineIntentResult result) {
        if (result.succeeded()) return result.data();
        return Map.of("error", Map.of("type", result.errorCode(), "message", result.errorMessage()));
    }


    /** The intent as a JSON string, from either an object or an already-serialized string. */
    private static String intentText(JsonNode intent) {
        if (intent == null || intent.isNull() || intent.isMissingNode()) {
            throw new IllegalArgumentException("Missing 'intent'.");
        }
        return intent.isTextual() ? intent.asText() : intent.toString();
    }

    private static String toJson(Object value) {
        try {
            return JSON.writeValueAsString(value);
        } catch (IOException e) {
            throw new IllegalStateException("Unable to serialize the intent result.", e);
        }
    }
}
