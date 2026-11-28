package com.foundgine.samples.supplychain.advanced;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.samples.supplychain.advanced.data.SupplyChainData;
import com.foundgine.samples.supplychain.advanced.intent.IntentCaller;
import com.foundgine.samples.supplychain.advanced.intent.SupplyChainIntentService;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.stream.IntStream;
import java.util.stream.Stream;

/**
 * Red-team penetration tests for the intent boundary. {@code IntentInjectionCorpusTest} asks whether
 * hostile <em>text</em> can change a query. These tests play an authenticated, low-privilege caller who
 * sends well-formed requests and tries to learn or reach something the policy denies:
 *
 * <ul>
 *   <li>inference through filters and ordering on unreadable fields and relationships;
 *   <li>wrong JSON types and serializer metadata keys;
 *   <li>parser errors that disclose implementation details;
 *   <li>ambiguous identity headers;
 *   <li>cross-caller bleed under concurrency.
 * </ul>
 *
 * <p>Every inference probe has a positive control showing that it does return information to an allowed
 * role. They run in process against the sample's in-memory provider, not over HTTP or against a
 * database. Mirrors the C# {@code IntentRedTeamPenetrationTests}.
 */
class IntentRedTeamPenetrationTest {
    private static final SupplyChainIntentService SERVICE =
            new SupplyChainIntentService(SupplyChainData.seed());

    private static final String HEAD =
            "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}]";
    private static final String SUPPLIER_NAMES = HEAD + "}";
    private static final String NAME_IS_KIWI =
            "{\"kind\":\"field\",\"field\":\"Name\",\"operator\":\"Eq\",\"value\":\"Kiwi Components\"}";
    private static final String CRITICAL_INCIDENT =
            "{\"kind\":\"relationship\",\"relationship\":\"incidents\",\"quantifier\":\"Some\","
                    + "\"predicate\":{\"kind\":\"field\",\"field\":\"Severity\",\"operator\":\"Eq\","
                    + "\"value\":\"Critical\"}}";

    private record Outcome(int status, String message, List<String> names) {}

    private static IntentCaller caller(String tenant, String role) {
        return IntentCaller.resolve(tenant, role, SERVICE.knownTenants());
    }

    @SuppressWarnings("unchecked")
    private static Outcome run(SupplyChainIntentService service, IntentCaller caller, String intent) {
        try {
            var result = service.execute(caller, intent).toCompletableFuture().join();
            var rows = (List<Map<String, Object>>) result.get("rows");
            return new Outcome(
                    200, null, rows.stream().map(r -> String.valueOf(r.get("Supplier.Name"))).toList());
        } catch (Throwable t) {
            var failure = SupplyChainIntentService.failure(t);
            return new Outcome(failure.status(), failure.message(), List.of());
        }
    }

    private static Outcome operator(String intent) {
        return run(SERVICE, caller("tenant-a", "warehouse_operator"), intent);
    }

    private static Outcome analyst(String intent) {
        return run(SERVICE, caller("tenant-a", "analyst"), intent);
    }

    private static String filtered(String filter) {
        return HEAD + ",\"filter\":" + filter + "}";
    }

    private static String ordered(String direction) {
        return HEAD + ",\"order\":[{\"field\":\"RiskScore\",\"direction\":\"" + direction + "\"}]}";
    }

    private static String risk(String operator, String value) {
        return "{\"kind\":\"field\",\"field\":\"RiskScore\",\"operator\":\"" + operator + "\",\"value\":" + value + "}";
    }

    private static void assertForbidden(String label, Outcome outcome) {
        assertEquals(403, outcome.status(), label + ": " + outcome);
        assertTrue(outcome.names().isEmpty(), label + ": " + outcome);
    }

    private static void assertRefused(String label, Outcome outcome) {
        assertTrue(
                outcome.status() == 400 || outcome.status() == 403,
                label + ": expected 400/403, got " + outcome);
    }

    // ----- inference attacks on data the role cannot read ----------------------------------------

    @Test
    void hiddenFieldCannotBeRecoveredByFiltering() {
        assertEquals(List.of("Pacific Semiconductors"), analyst(filtered(risk("Eq", "0.87"))).names());
        assertEquals(List.of(), analyst(filtered(risk("Eq", "0.5"))).names());

        assertForbidden("Eq hit", operator(filtered(risk("Eq", "0.87"))));
        assertForbidden("Eq miss", operator(filtered(risk("Eq", "0.5"))));
        assertForbidden("Neq", operator(filtered(risk("Neq", "0.87"))));
        assertForbidden("In", operator(filtered(risk("In", "[0.87,0.5]"))));
    }

    @ParameterizedTest
    @ValueSource(strings = {"and", "or"})
    void hiddenFieldStaysHiddenWhenNestedInsideABooleanFilter(String kind) {
        var nested =
                "{\"kind\":\"" + kind + "\",\"expressions\":[" + NAME_IS_KIWI
                        + ",{\"kind\":\"and\",\"expressions\":[" + risk("Eq", "0.87") + "]}]}";
        assertForbidden(kind, operator(filtered(nested)));
    }

    @ParameterizedTest
    @ValueSource(strings = {"Asc", "Desc"})
    void hiddenFieldCannotBeRankedByOrdering(String direction) {
        // Positive control: the analyst's order really depends on the score.
        var expected = new ArrayList<>(List.of("Kiwi Components", "Pacific Semiconductors"));
        if (direction.equals("Desc")) java.util.Collections.reverse(expected);
        assertEquals(expected, analyst(ordered(direction)).names());

        assertForbidden("order " + direction, operator(ordered(direction)));
    }

    @ParameterizedTest
    @ValueSource(strings = {"Some", "None", "All"})
    void hiddenRelationshipCannotBeProbedWithARelationshipFilter(String quantifier) {
        assertForbidden(
                "incidents " + quantifier,
                operator(filtered(CRITICAL_INCIDENT.replace("\"Some\"", "\"" + quantifier + "\""))));
    }

    @Test
    @SuppressWarnings("unchecked")
    void explainAndCapabilitiesDoNotOpenAWayRoundThePolicy() {
        var warehouse = caller("tenant-a", "warehouse_operator");
        for (var intent : List.of(filtered(risk("Eq", "0.87")), ordered("Asc"), filtered(CRITICAL_INCIDENT))) {
            var error = assertThrows(Throwable.class, () -> SERVICE.explain(warehouse, intent), intent);
            assertEquals(403, SupplyChainIntentService.failure(error).status(), intent);
        }

        var entities = (List<Map<String, Object>>) SERVICE.capabilities(warehouse).get("entities");
        var supplier =
                entities.stream().filter(e -> "Supplier".equals(e.get("entity"))).findFirst().orElseThrow();
        var read = (Map<String, Object>) supplier.get("read");
        assertFalse(((List<String>) read.get("fields")).contains("RiskScore"));
        assertFalse(((List<String>) read.get("relationships")).contains("incidents"));

        assertEquals(403, operator(filtered(risk("Eq", "0.87"))).status());
    }

    // ----- type confusion and parser disclosure ------------------------------------------------------

    static Stream<String> wrongTypes() {
        return Stream.of(
                ",\"limit\":\"abc\"}",
                ",\"limit\":{}}",
                ",\"limit\":true}",
                ",\"limit\":-0.5}",
                ",\"limit\":4294967296}",
                ",\"offset\":[1]}",
                ",\"order\":\"RiskScore\"}",
                ",\"order\":{\"field\":\"Name\",\"direction\":\"Asc\"}}",
                ",\"order\":[\"Name\"]}",
                ",\"order\":[{\"field\":\"Name\",\"direction\":\"Asc\",\"relationshipPath\":\"x\"}]}",
                ",\"order\":[{\"field\":\"Name\",\"direction\":999}]}",
                ",\"filter\":\"Name = 'x'\"}",
                ",\"filter\":[]}",
                ",\"filter\":{\"kind\":\"field\",\"field\":\"Name\",\"operator\":999,\"value\":\"x\"}}",
                ",\"filter\":{\"kind\":\"field\",\"field\":\"Name\",\"operator\":\"999\",\"value\":\"x\"}}",
                ",\"filter\":{\"kind\":\"field\",\"field\":{},\"operator\":\"Eq\",\"value\":\"x\"}}",
                ",\"filter\":{\"kind\":1}}",
                ",\"after\":{}}",
                "} {\"rootEntity\":\"Product\"}");
    }

    @ParameterizedTest(name = "[{index}] wrong JSON type is refused: {0}")
    @MethodSource("wrongTypes")
    void wrongJsonTypesAreRefusedNotCoercedOrIgnored(String tail) {
        assertRefused(tail, analyst(HEAD + tail));
    }

    @Test
    void childrenOfTheWrongTypeAreRefused() {
        assertRefused(
                "children as string",
                analyst(
                        "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\",\"children\":\"RiskScore\"}]}"));
    }

    @ParameterizedTest
    @ValueSource(strings = {"__proto__", "constructor", "prototype", "$type", "@class", "$where", "tenantId "})
    void serializerMetadataKeysAreRejectedEverywhere(String key) {
        var member = "\"" + key + "\":\"java.lang.Runtime\"";
        for (var intent :
                List.of(
                        HEAD + "," + member + "}",
                        "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"," + member + "}]}",
                        filtered(NAME_IS_KIWI.substring(0, NAME_IS_KIWI.length() - 1) + "," + member + "}"),
                        HEAD + ",\"order\":[{\"field\":\"Name\",\"direction\":\"Asc\"," + member + "}]}")) {
            assertRefused(key, analyst(intent));
        }
    }

    @ParameterizedTest(name = "[{index}] parser error discloses nothing")
    @ValueSource(
            strings = {
                "{",
                "{\"rootEntity\":}",
                "{CTRL}",
                "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>",
                "rootEntity=Supplier",
                "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}],\"limit\":1e99999}"
            })
    void parserErrorsDoNotDiscloseImplementationDetails(String body) {
        var outcome = analyst(body.replace("{CTRL}", "\u0001\u0002"));
        assertRefused(body, outcome);
        var message = String.valueOf(outcome.message());
        for (var marker :
                List.of(
                        "System.", "Microsoft.", "com.foundgine", "com.fasterxml", "java.", "Source:",
                        "BytePosition", "LineNumber", "[line:", "JsonToken", "\n\tat ", "root:x:")) {
            assertFalse(message.contains(marker), "leaked '" + marker + "': " + message);
        }
    }

    // ----- identity ----------------------------------------------------------------------------------

    @ParameterizedTest(name = "[{index}] ambiguous identity header is refused")
    @CsvSource(
            delimiter = '|',
            value = {
                "tenant-a|analyst,supply_chain_manager",
                "tenant-a|customer analyst",
                "tenant-a|1",
                "tenant-a|analyst{CRLF}X-Foundgine-Role: supply_chain_manager",
                "tenant-a|analyst{NUL}",
                "tenant-a|ａnalyst",
                "tenant-a,tenant-b|analyst",
                "TENANT-B|analyst",
                "tenant-a{CRLF}tenant-b|analyst",
                "*|analyst"
            })
    void ambiguousIdentityHeadersNeverResolveToACaller(String tenant, String role) {
        var hostileTenant = tenant.replace("{CRLF}", "\r\n");
        var hostileRole = role.replace("{CRLF}", "\r\n").replace("{NUL}", "\u0000");
        assertThrows(SecurityException.class, () -> caller(hostileTenant, hostileRole));
    }

    @ParameterizedTest
    @ValueSource(
            strings = {
                "\"tenant\":\"tenant-b\",\"Tenant\":\"tenant-b\"",
                "\"TENANT\":\"tenant-b\"",
                "\"Role\":\"supply_chain_manager\""
            })
    void identityInTheBodyIsRefusedInAnyCasing(String member) {
        assertRefused(member, analyst(HEAD + "," + member + "}"));
    }

    @Test
    void duplicateRootEntityCannotSlipPastADenial() {
        var outcome =
                run(
                        SERVICE,
                        caller("tenant-a", "customer"),
                        "{\"rootEntity\":\"Product\",\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}]}");
        assertRefused("duplicate rootEntity", outcome);
        assertTrue(outcome.names().isEmpty());
    }

    // ----- concurrency -----------------------------------------------------------------------------

    @Test
    void concurrentCallersNeverReceiveEachOthersResults() throws Exception {
        var callers =
                List.of(
                        caller("tenant-a", "analyst"),
                        caller("tenant-b", "analyst"),
                        caller("tenant-a", "warehouse_operator"),
                        caller("tenant-a", "customer"));
        var expected = callers.stream().map(c -> run(SERVICE, c, SUPPLIER_NAMES)).toList();
        assertNotEquals(expected.get(0).names(), expected.get(1).names());
        assertEquals(403, expected.get(3).status());

        // A fresh service, so engine creation races too.
        var service = new SupplyChainIntentService(SupplyChainData.seed());
        var pool = Executors.newFixedThreadPool(16);
        try {
            var tasks =
                    IntStream.range(0, 400)
                            .mapToObj(i -> pool.submit(() -> run(service, callers.get(i % 4), SUPPLIER_NAMES)))
                            .toList();
            for (int i = 0; i < tasks.size(); i++) {
                var actual = tasks.get(i).get(30, java.util.concurrent.TimeUnit.SECONDS);
                assertEquals(expected.get(i % 4).status(), actual.status(), "request " + i);
                assertEquals(expected.get(i % 4).names(), actual.names(), "request " + i);
            }
        } finally {
            pool.shutdownNow();
        }
    }
}
