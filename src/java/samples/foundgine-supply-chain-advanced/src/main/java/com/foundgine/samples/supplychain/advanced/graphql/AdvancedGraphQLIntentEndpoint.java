package com.foundgine.samples.supplychain.advanced.graphql;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.foundgine.runtime.FoundgineIntentOutcome;
import com.foundgine.runtime.FoundgineIntentResult;
import com.foundgine.samples.supplychain.advanced.intent.IntentCaller;
import com.foundgine.samples.supplychain.advanced.mcp.AdvancedIntentEndpoint;
import graphql.ExecutionInput;
import graphql.GraphQL;
import graphql.schema.DataFetcher;
import graphql.schema.idl.RuntimeWiring;
import graphql.schema.idl.SchemaGenerator;
import graphql.schema.idl.SchemaParser;
import graphql.schema.idl.TypeRuntimeWiring;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;
import com.sun.net.httpserver.HttpServer;

import java.io.IOException;
import java.io.OutputStream;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Objects;

/** GraphQL transport adapter for the same runtime intent boundary used by HTTP and MCP. */
public final class AdvancedGraphQLIntentEndpoint implements HttpHandler {
    public static final String PATH = "/graphql";
    private static final int MAX_BODY_BYTES = 1 << 20;
    private static final String SCHEMA = """
            type Query {
              foundgineIntent(mode: String!, intentJson: String): FoundgineIntentResult!
            }
            type Mutation {
              foundgineMutation(mutationJson: String!): FoundgineIntentResult!
            }
            type FoundgineIntentResult {
              outcome: String!
              data: String
              errorCode: String
              errorMessage: String
            }
            """;

    private final AdvancedIntentEndpoint intents;
    private final ObjectMapper json = new ObjectMapper().findAndRegisterModules();

    public AdvancedGraphQLIntentEndpoint(AdvancedIntentEndpoint intents) {
        this.intents = Objects.requireNonNull(intents, "intents");
    }

    public void register(HttpServer server) {
        server.createContext(PATH, this);
    }

    @Override
    public void handle(HttpExchange exchange) throws IOException {
        try (exchange) {
            try {
                if (!"POST".equalsIgnoreCase(exchange.getRequestMethod())) {
                    exchange.getResponseHeaders().set("Allow", "POST");
                    write(exchange, 405, Map.of("errors", new Object[]{Map.of("message", "Only POST is supported.")}));
                    return;
                }

                byte[] body = exchange.getRequestBody().readNBytes(MAX_BODY_BYTES + 1);
                if (body.length > MAX_BODY_BYTES) {
                    write(exchange, 413, Map.of("errors", new Object[]{Map.of("message", "GraphQL request body is too large.")}));
                    return;
                }

                JsonNode request;
                try {
                    request = json.readTree(body);
                } catch (Exception malformed) {
                    write(exchange, 400, Map.of("errors", new Object[]{Map.of("message", "GraphQL request must be valid JSON.")}));
                    return;
                }
                if (request == null || !request.isObject() || !request.hasNonNull("query")) {
                    write(exchange, 400, Map.of("errors", new Object[]{Map.of("message", "A GraphQL query is required.")}));
                    return;
                }

                String query = request.path("query").asText();
                if (query.isBlank()) {
                    write(exchange, 400, Map.of("errors", new Object[]{Map.of("message", "A non-empty GraphQL query is required.")}));
                    return;
                }
                JsonNode variablesNode = request.get("variables");
                if (variablesNode != null && !variablesNode.isNull() && !variablesNode.isObject()) {
                    write(exchange, 400, Map.of("errors", new Object[]{Map.of("message", "GraphQL variables must be a JSON object.")}));
                    return;
                }
                Map<String, Object> variables = variablesNode != null && !variablesNode.isNull()
                        ? json.convertValue(variablesNode, new TypeReference<LinkedHashMap<String, Object>>() {})
                        : Map.of();

                IntentCaller caller = null;
                FoundgineIntentResult authenticationFailure = null;
                try {
                    caller = IntentCaller.resolve(
                            exchange.getRequestHeaders().getFirst(IntentCaller.TENANT_HEADER),
                            exchange.getRequestHeaders().getFirst(IntentCaller.ROLE_HEADER),
                            intents.knownTenants());
                } catch (SecurityException denied) {
                    authenticationFailure = FoundgineIntentResult.failure(
                            FoundgineIntentOutcome.FORBIDDEN, "forbidden", "The supplied caller context is not permitted.");
                }

                String correlationId = correlationId(exchange);
                GraphQL graphQL = graphQL(caller, authenticationFailure, correlationId);
                ExecutionInput input = ExecutionInput.newExecutionInput()
                        .query(query)
                        .variables(variables)
                        .build();
                var result = graphQL.execute(input);
                write(exchange, 200, result.toSpecification());
            } catch (RuntimeException failure) {
                write(exchange, 500, Map.of("errors", new Object[]{Map.of("message", "The GraphQL request could not be processed.")}));
            }
        }
    }

    private GraphQL graphQL(IntentCaller caller, FoundgineIntentResult authenticationFailure, String correlationId) {
        DataFetcher<Map<String, Object>> resolver = environment -> {
            if (authenticationFailure != null) return shape(authenticationFailure);
            String mode = environment.getArgument("mode");
            String intentJson = environment.getArgument("intentJson");
            return shape(intents.invoke(caller, mode, intentJson, correlationId));
        };
        DataFetcher<Map<String, Object>> mutationResolver = environment -> {
            if (authenticationFailure != null) return shape(authenticationFailure);
            String mutationJson = environment.getArgument("mutationJson");
            return shape(intents.invokeMutation(caller, mutationJson, correlationId));
        };
        var wiring = RuntimeWiring.newRuntimeWiring()
                .type(TypeRuntimeWiring.newTypeWiring("Query").dataFetcher("foundgineIntent", resolver))
                .type(TypeRuntimeWiring.newTypeWiring("Mutation").dataFetcher("foundgineMutation", mutationResolver))
                .build();
        var schema = new SchemaGenerator().makeExecutableSchema(new SchemaParser().parse(SCHEMA), wiring);
        return GraphQL.newGraphQL(schema).build();
    }

    private static String correlationId(HttpExchange exchange) {
        String supplied = exchange.getRequestHeaders().getFirst("X-Correlation-ID");
        if (supplied == null || supplied.isBlank()) return java.util.UUID.randomUUID().toString();
        if (supplied.length() > 128 || !supplied.matches("[A-Za-z0-9._:-]+"))
            throw new IllegalArgumentException("X-Correlation-ID must be 1-128 safe identifier characters.");
        return supplied;
    }

    private Map<String, Object> shape(FoundgineIntentResult result) {
        var value = new LinkedHashMap<String, Object>();
        value.put("outcome", result.outcome().name());
        if (result.data() != null) {
            try {
                value.put("data", json.writeValueAsString(result.data()));
            } catch (IOException e) {
                value.put("outcome", FoundgineIntentOutcome.FAILED.name());
                value.put("errorCode", "serialization_failed");
                value.put("errorMessage", "The intent result could not be serialized.");
                return value;
            }
        }
        value.put("errorCode", result.errorCode());
        value.put("errorMessage", result.errorMessage());
        return value;
    }

    private void write(HttpExchange exchange, int status, Object body) throws IOException {
        byte[] bytes = json.writeValueAsBytes(body);
        exchange.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
        exchange.sendResponseHeaders(status, bytes.length);
        try (OutputStream output = exchange.getResponseBody()) {
            output.write(bytes);
        }
    }
}
