package com.foundgine.samples.supplychain.advanced;

import static org.junit.jupiter.api.Assertions.*;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.foundgine.samples.supplychain.advanced.data.SupplyChainData;
import com.foundgine.samples.supplychain.advanced.intent.IntentCaller;
import com.foundgine.samples.supplychain.advanced.intent.SupplyChainIntentService;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.stream.Collectors;
import java.util.stream.Stream;

/**
 * Intent-boundary injection corpus: the "sqlmap-shaped" gap in the Supply Chain sample.
 *
 * <p>Scanners such as sqlmap assume a request parameter ends up inside a query string. Foundgine
 * callers send structured intent instead, so hostile text can only ever be (a) a <em>value</em>,
 * which must stay data, or (b) a <em>name</em> (entity, field, relationship), which must be
 * resolved against the semantic model and refused when unknown. This suite runs one shared
 * payload corpus through every slot of the intent and checks the invariants the architecture
 * promises:
 *
 * <ol>
 *   <li>A payload never produces an unclassified failure (HTTP 500 / {@code internal_error}).
 *   <li>A payload never widens a result beyond what the caller's policy and tenant allow.
 *   <li>A payload never turns a denial into success.
 *   <li>Failure messages never expose stack traces, driver names or another tenant's data.
 *   <li>Structural and resource abuse fails closed at the parser, before planning.
 *   <li>Hostile identity headers are refused; identity inside the intent body is refused.
 *   <li>A fixed canary read is unchanged after the whole corpus has run.
 * </ol>
 *
 * <p>The in-memory provider used by this sample cannot show SQL text, so these tests prove the
 * boundary properties (values stay data, names are resolved, tenancy comes from policy). The SQL
 * provider conformance lives in {@code Foundgine.E2E.Tests/Security/SqlSecurityConformanceTests}.
 *
 * <p>Keep {@link #VALUE_PAYLOADS} identical to the C# {@code IntentInjectionCorpusTests}.
 */
class IntentInjectionCorpusTest {
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final SupplyChainIntentService SERVICE =
            new SupplyChainIntentService(SupplyChainData.seed());

    private static final Set<String> TENANT_A_SUPPLIERS =
            Set.of("Kiwi Components", "Pacific Semiconductors");
    private static final String TENANT_B_SUPPLIER = "Global Metals";

    /** Hostile text. Identical in the C# suite; no entry may equal a real entity, field or tenant. */
    static final List<String> VALUE_PAYLOADS =
            List.of(
                    "' OR '1'='1",
                    "' OR 1=1--",
                    "\" OR \"\"=\"",
                    "') OR ('1'='1",
                    "'; DROP TABLE Supplier;--",
                    "1; SELECT pg_sleep(5)--",
                    "' UNION SELECT NULL,NULL,NULL--",
                    "Kiwi Components' --",
                    "\\' OR 1=1 /*",
                    "'+(SELECT 1)+'",
                    "%27%20OR%201=1",
                    "{\"$ne\":null}",
                    "{\"$gt\":\"\"}",
                    "}) MATCH (n) RETURN n //",
                    "../../etc/passwd",
                    "${jndi:ldap://127.0.0.1/a}",
                    "{{7*7}}",
                    "<script>alert(1)</script>",
                    "tenant-b",
                    "tenant-a' OR tenant='tenant-b",
                    "\u0000",
                    "Kiwi\u0000Components",
                    "\u202Egnp.exe",
                    "\uD83D\uDE00",
                    "%",
                    "_",
                    "*",
                    "%00",
                    "A".repeat(10_000));

    private static final List<String> ROLES =
            List.of("customer", "analyst", "warehouse_operator", "supply_chain_manager");

    static Stream<String> valuePayloads() {
        return VALUE_PAYLOADS.stream();
    }

    // ----- harness -------------------------------------------------------------------------

    private record Outcome(int status, String type, String message, List<Map<String, Object>> rows) {
        boolean succeeded() {
            return status == 200;
        }
    }

    private static IntentCaller caller(String tenant, String role) {
        return IntentCaller.resolve(tenant, role, SERVICE.knownTenants());
    }

    @SuppressWarnings("unchecked")
    private static Outcome run(IntentCaller caller, String intentJson) {
        try {
            var result = SERVICE.execute(caller, intentJson).toCompletableFuture().join();
            return new Outcome(200, null, null, (List<Map<String, Object>>) result.get("rows"));
        } catch (Throwable t) {
            var failure = SupplyChainIntentService.failure(t);
            return new Outcome(failure.status(), failure.type(), failure.message(), List.of());
        }
    }

    private static Outcome run(IntentCaller caller, JsonNode intent) {
        return run(caller, intent.toString());
    }

    private static ObjectNode intent(String root, String... fields) {
        var node = JSON.createObjectNode();
        node.put("rootEntity", root);
        ArrayNode selections = node.putArray("selections");
        for (var field : fields) selections.addObject().put("field", field);
        return node;
    }

    private static ObjectNode fieldFilter(String field, String operator, Object value) {
        var filter = JSON.createObjectNode();
        filter.put("kind", "field");
        filter.put("field", field);
        filter.put("operator", operator);
        filter.set("value", JSON.valueToTree(value));
        return filter;
    }

    private static ObjectNode withFilter(ObjectNode intent, JsonNode filter) {
        intent.set("filter", filter);
        return intent;
    }

    private static List<String> names(Outcome outcome, String column) {
        return outcome.rows().stream().map(r -> String.valueOf(r.get(column))).toList();
    }

    /** What an unauthenticated attacker must never learn from an error. */
    private static void assertNoInternalsLeak(String label, Outcome outcome) {
        if (outcome.message() == null) return;
        var m = outcome.message();
        for (var marker :
                List.of(
                        "\n\tat ",
                        "\n   at ",
                        "Npgsql",
                        "org.postgresql",
                        "java.sql.",
                        "jdbc:",
                        "EntityId[",
                        TENANT_B_SUPPLIER)) {
            assertFalse(m.contains(marker), label + ": error message leaked '" + marker + "'");
        }
    }

    /** Never 500, never a leak; either a classified refusal or a clean success. */
    private static void assertClassified(String label, Outcome outcome) {
        assertNotEquals(500, outcome.status(), label + ": unclassified failure " + outcome.message());
        assertTrue(
                outcome.succeeded() || outcome.status() == 400 || outcome.status() == 403,
                label + ": unexpected status " + outcome.status());
        assertNoInternalsLeak(label, outcome);
    }

    private static void assertFailsClosed(String label, Outcome outcome) {
        assertFalse(outcome.succeeded(), label + ": hostile input was accepted");
        assertTrue(
                outcome.status() == 400 || outcome.status() == 403,
                label + ": expected 400/403 but got " + outcome.status() + " " + outcome.message());
        assertNoInternalsLeak(label, outcome);
    }

    private static String label(String slot, String payload) {
        var shown = payload.length() > 40 ? payload.substring(0, 40) + "...(" + payload.length() + ")" : payload;
        return slot + " <- " + shown.replace("\u0000", "\\0");
    }

    // ----- sanity: the harness can actually see an attack succeed -----------------------------

    @Test
    void harnessSanityFilterBitesAndTenantAIsolatedFromB() {
        var analyst = caller("tenant-a", "analyst");

        var hit =
                run(
                        analyst,
                        withFilter(
                                intent("Product", "Sku"),
                                fieldFilter("Category", "Eq", "Electronics")));
        assertTrue(hit.succeeded(), String.valueOf(hit.message()));
        assertFalse(hit.rows().isEmpty(), "A legitimate filter must match something");

        var miss =
                run(
                        analyst,
                        withFilter(
                                intent("Product", "Sku"),
                                fieldFilter("Category", "Eq", "no-such-category")));
        assertTrue(miss.succeeded());
        assertTrue(miss.rows().isEmpty(), "A filter that matches nothing must return nothing");

        var all = run(analyst, intent("Supplier", "Name"));
        assertEquals(TENANT_A_SUPPLIERS, Set.copyOf(names(all, "Supplier.Name")));
    }

    // ----- values stay data -----------------------------------------------------------------------

    @ParameterizedTest(name = "[{index}] string value stays data")
    @MethodSource("valuePayloads")
    void stringFilterValueIsDataNotQuery(String payload) {
        var outcome =
                run(
                        caller("tenant-a", "analyst"),
                        withFilter(intent("Product", "Sku"), fieldFilter("Category", "Eq", payload)));

        var label = label("Product.Category Eq", payload);
        assertClassified(label, outcome);
        if (outcome.succeeded()) {
            assertTrue(
                    outcome.rows().isEmpty(),
                    label + ": matched " + outcome.rows().size() + " rows; the value acted like a pattern or query");
        }
    }

    @ParameterizedTest(name = "[{index}] cannot cross tenants via Eq")
    @MethodSource("valuePayloads")
    void tenantBoundaryHoldsForEqPayloads(String payload) {
        var outcome =
                run(
                        caller("tenant-a", "analyst"),
                        withFilter(intent("Supplier", "Name"), fieldFilter("Name", "Eq", payload)));

        var label = label("Supplier.Name Eq", payload);
        assertClassified(label, outcome);
        if (outcome.succeeded()) {
            var seen = names(outcome, "Supplier.Name");
            assertFalse(seen.contains(TENANT_B_SUPPLIER), label + ": tenant-b data returned");
            assertTrue(TENANT_A_SUPPLIERS.containsAll(seen), label + ": unexpected rows " + seen);
            assertTrue(
                    seen.size() < TENANT_A_SUPPLIERS.size(),
                    label + ": a tautology returned every supplier");
        }
    }

    @ParameterizedTest(name = "[{index}] tautology via OR stays inside the tenant")
    @MethodSource("valuePayloads")
    void tautologyViaOrNeverEscapesTheTenantPredicate(String payload) {
        // (Name = payload) OR (Name <> payload) is true for every row the caller may see, which is
        // exactly the tenant's rows. Anything extra means the OR was able to override the policy.
        var either = JSON.createObjectNode();
        either.put("kind", "or");
        either.putArray("expressions")
                .add(fieldFilter("Name", "Eq", payload))
                .add(fieldFilter("Name", "Neq", payload));

        var outcome = run(caller("tenant-a", "analyst"), withFilter(intent("Supplier", "Name"), either));

        var label = label("Supplier.Name OR tautology", payload);
        assertClassified(label, outcome);
        if (outcome.succeeded()) {
            assertEquals(
                    TENANT_A_SUPPLIERS,
                    Set.copyOf(names(outcome, "Supplier.Name")),
                    label + ": result differs from the caller's tenant");
        }
    }

    @ParameterizedTest(name = "[{index}] denial survives payload")
    @MethodSource("valuePayloads")
    void deniedRoleStaysDeniedWhateverTheValue(String payload) {
        // CUSTOMER may not read Supplier at all (see CanonicalIntentEndpointTest).
        var outcome =
                run(
                        caller("tenant-a", "customer"),
                        withFilter(intent("Supplier", "Name"), fieldFilter("Name", "Eq", payload)));

        var label = label("customer Supplier Eq", payload);
        assertFalse(outcome.succeeded(), label + ": a denial was turned into success");
        assertEquals(403, outcome.status(), label);
        assertNoInternalsLeak(label, outcome);
    }

    // ----- names are resolved, never interpreted ------------------------------------------------

    @ParameterizedTest(name = "[{index}] hostile name refused")
    @MethodSource("valuePayloads")
    void hostileNamesAreRefusedInEverySlot(String payload) {
        var analyst = caller("tenant-a", "analyst");

        var rootEntity = JSON.createObjectNode();
        rootEntity.put("rootEntity", payload);
        rootEntity.putArray("selections").addObject().put("field", "Name");

        var selectionField = intent("Supplier", payload);

        var relationship = intent("Product", "Name");
        ((ArrayNode) relationship.get("selections"))
                .addObject()
                .put("relationship", payload)
                .putArray("children")
                .addObject()
                .put("field", "Status");

        var filterField = withFilter(intent("Supplier", "Name"), fieldFilter(payload, "Eq", "x"));

        var orderField = intent("Supplier", "Name");
        orderField.putArray("order").addObject().put("field", payload).put("direction", "Asc");

        assertFailsClosed(label("rootEntity", payload), run(analyst, rootEntity));
        assertFailsClosed(label("selection.field", payload), run(analyst, selectionField));
        assertFailsClosed(label("selection.relationship", payload), run(analyst, relationship));
        assertFailsClosed(label("filter.field", payload), run(analyst, filterField));
        assertFailsClosed(label("order.field", payload), run(analyst, orderField));
    }

    @ParameterizedTest(name = "[{index}] hostile operator/direction refused")
    @MethodSource("valuePayloads")
    void hostileOperatorAndDirectionAreRefused(String payload) {
        var analyst = caller("tenant-a", "analyst");

        var badOperator =
                withFilter(intent("Supplier", "Name"), fieldFilter("Name", payload, "x"));
        var badDirection = intent("Supplier", "Name");
        badDirection.putArray("order").addObject().put("field", "Name").put("direction", payload);
        var badKind = withFilter(intent("Supplier", "Name"), fieldFilter("Name", "Eq", "x"));
        ((ObjectNode) badKind.get("filter")).put("kind", payload);

        assertFailsClosed(label("filter.operator", payload), run(analyst, badOperator));
        assertFailsClosed(label("order.direction", payload), run(analyst, badDirection));
        assertFailsClosed(label("filter.kind", payload), run(analyst, badKind));
    }

    // ----- identity never comes from the body ------------------------------------------------------

    @Test
    void identityShapedPropertiesAreRejectedAtRootAndInFilters() {
        var analyst = caller("tenant-a", "analyst");
        for (var key : List.of("tenant", "tenantId", "role", "actor", "isAdmin", "permissions", "scope")) {
            var atRoot = intent("Supplier", "Name");
            atRoot.put(key, "tenant-b");
            var root = run(analyst, atRoot);
            assertEquals(400, root.status(), "root." + key);
            assertEquals("invalid_intent", root.type(), "root." + key);

            var inFilter = withFilter(intent("Supplier", "Name"), fieldFilter("Name", "Eq", "x"));
            ((ObjectNode) inFilter.get("filter")).put(key, "tenant-b");
            assertFailsClosed("filter." + key, run(analyst, inFilter));
        }
    }

    @ParameterizedTest(name = "[{index}] hostile tenant/role header refused")
    @MethodSource("valuePayloads")
    void hostileIdentityHeadersAreRefused(String payload) {
        // "tenant-b" is a real tenant, so it is a legitimate header value, not an injection.
        if (payload.equals("tenant-b")) return;
        var known = SERVICE.knownTenants();
        assertThrows(
                SecurityException.class,
                () -> IntentCaller.resolve(payload, "analyst", known),
                label("tenant header", payload));
        assertThrows(
                SecurityException.class,
                () -> IntentCaller.resolve("tenant-a", payload, known),
                label("role header", payload));
    }

    // ----- structural abuse and resource limits fail closed ------------------------------------------

    static Stream<String> malformedIntents() {
        var deepFilter = new StringBuilder();
        int depth = 500;
        for (int i = 0; i < depth; i++) deepFilter.append("{\"kind\":\"and\",\"expressions\":[");
        deepFilter.append("{\"kind\":\"field\",\"field\":\"Name\",\"operator\":\"Eq\",\"value\":\"x\"}");
        for (int i = 0; i < depth; i++) deepFilter.append("]}");

        var wideFilter = new StringBuilder("{\"kind\":\"or\",\"expressions\":[");
        for (int i = 0; i < 20_000; i++) {
            if (i > 0) wideFilter.append(',');
            wideFilter.append("{\"kind\":\"field\",\"field\":\"Name\",\"operator\":\"Eq\",\"value\":\"x\"}");
        }
        wideFilter.append("]}");

        var manySelections = new StringBuilder();
        for (int i = 0; i < 50_000; i++) {
            if (i > 0) manySelections.append(',');
            manySelections.append("{\"field\":\"Name\"}");
        }

        return Stream.of(
                "",
                "   ",
                "{",
                "null",
                "[]",
                "42",
                "\"Supplier\"",
                "{}",
                "{\"rootEntity\":null,\"selections\":[{\"field\":\"Name\"}]}",
                "{\"rootEntity\":\"Supplier\",\"selections\":\"Name\"}",
                "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}],\"limit\":-1}",
                "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}],\"offset\":-1}",
                "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}],\"filter\":" + deepFilter + "}",
                "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}],\"filter\":" + wideFilter + "}",
                "{\"rootEntity\":\"Supplier\",\"selections\":[" + manySelections + "]}",
                "{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}],\"unexpected\":{\"a\":1}}");
    }

    @ParameterizedTest(name = "[{index}] malformed or oversized intent fails closed")
    @MethodSource("malformedIntents")
    void malformedAndOversizedIntentsFailClosed(String raw) {
        var shown = raw.length() > 60 ? raw.substring(0, 60) + "...(" + raw.length() + " chars)" : raw;
        assertFailsClosed("intent " + shown, run(caller("tenant-a", "analyst"), raw));
    }

    // ----- policy side channels ------------------------------------------------------------------

    @Test
    void filteringOnAFieldTheCallerCannotReadDoesNotLeakItsValue() {
        // WAREHOUSE_OPERATOR cannot read Supplier.RiskScore (the policy prunes it from results).
        // If it could still *filter* on it, two probes would differ and the value could be
        // recovered by bisection. Acceptable outcomes: the filter is refused, or it is ignored so
        // both probes return the same rows. A difference is a real side channel.
        var operator = caller("tenant-a", "warehouse_operator");
        var everything =
                run(operator, withFilter(intent("Supplier", "Name"), fieldFilter("RiskScore", "Gt", -1)));
        var nothing =
                run(operator, withFilter(intent("Supplier", "Name"), fieldFilter("RiskScore", "Gt", 1_000_000_000)));

        assertClassified("operator RiskScore Gt -1", everything);
        assertClassified("operator RiskScore Gt 1e9", nothing);
        if (everything.succeeded() && nothing.succeeded()) {
            assertEquals(
                    everything.rows().size(),
                    nothing.rows().size(),
                    "Row count depends on a field this role may not read: filter side channel");
        }
    }

    @Test
    void everyRoleStaysInsideItsTenantAndNeverSeesAnotherTenantsSuppliers() {
        for (var tenant : List.of("tenant-a", "tenant-b")) {
            var own = tenant.equals("tenant-a") ? TENANT_A_SUPPLIERS : Set.of(TENANT_B_SUPPLIER);
            for (var role : ROLES) {
                var outcome = run(caller(tenant, role), intent("Supplier", "Name"));
                var label = tenant + "/" + role;
                assertClassified(label, outcome);
                if (outcome.succeeded()) {
                    assertTrue(
                            own.containsAll(names(outcome, "Supplier.Name")),
                            label + ": rows outside the caller's tenant");
                }
            }
        }
    }

    // ----- nothing the corpus does is remembered -----------------------------------------------------

    @Test
    void canaryReadsAreUnchangedAfterTheWholeCorpusHasRun() {
        var analyst = caller("tenant-a", "analyst");
        var before = run(analyst, intent("Supplier", "Name", "RiskScore"));
        var productsBefore = run(analyst, intent("Product", "Sku"));
        var customerBefore = run(caller("tenant-a", "customer"), intent("Supplier", "Name"));

        for (var payload : VALUE_PAYLOADS) {
            run(analyst, withFilter(intent("Supplier", "Name"), fieldFilter("Name", "Eq", payload)));
            run(analyst, withFilter(intent("Product", "Sku"), fieldFilter("Category", "Eq", payload)));
            run(analyst, intent("Supplier", payload));
        }
        for (var raw : malformedIntents().collect(Collectors.toList())) run(analyst, raw);

        assertEquals(before.rows(), run(analyst, intent("Supplier", "Name", "RiskScore")).rows());
        assertEquals(productsBefore.rows(), run(analyst, intent("Product", "Sku")).rows());
        var customerAfter = run(caller("tenant-a", "customer"), intent("Supplier", "Name"));
        assertEquals(customerBefore.status(), customerAfter.status());
        assertEquals(403, customerAfter.status(), "A denied role must stay denied after an attack run");
    }
}
