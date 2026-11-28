using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using Foundgine.SupplyChain.Advanced.Data;
using Foundgine.SupplyChain.Advanced.Intent;
using Xunit;

namespace Foundgine.SupplyChain.Advanced.Tests;

/// <summary>
/// Intent-boundary injection corpus: the "sqlmap-shaped" gap in the Supply Chain sample.
///
/// Scanners such as sqlmap assume a request parameter ends up inside a query string. Foundgine callers send
/// structured intent instead, so hostile text can only ever be (a) a <em>value</em>, which must stay data, or
/// (b) a <em>name</em> (entity, field, relationship), which must be resolved against the semantic model and
/// refused when unknown. This suite runs one shared payload corpus through every slot of the intent and checks:
/// <list type="number">
///   <item>A payload never produces an unclassified failure (HTTP 500 / <c>internal_error</c>).</item>
///   <item>A payload never widens a result beyond what the caller's policy and tenant allow.</item>
///   <item>A payload never turns a denial into success.</item>
///   <item>Failure messages never expose stack traces, driver names or another tenant's data.</item>
///   <item>Structural and resource abuse fails closed at the parser, before planning.</item>
///   <item>Hostile identity headers are refused; identity inside the intent body is refused.</item>
///   <item>A fixed canary read is unchanged after the whole corpus has run.</item>
/// </list>
/// The in-memory provider used by this sample cannot show SQL text, so these tests prove the boundary properties
/// (values stay data, names are resolved, tenancy comes from policy). SQL provider conformance lives in
/// <c>Foundgine.E2E.Tests/Security/SqlSecurityConformanceTests</c>.
///
/// Mirrors the Java <c>IntentInjectionCorpusTest</c>; keep <see cref="ValuePayloadList"/> identical.
/// </summary>
public sealed class IntentInjectionCorpusTests
{
    private static readonly SupplyChainIntentService Service = new(SupplyChainData.Seed());

    private static readonly HashSet<string> TenantASuppliers = ["Kiwi Components", "Pacific Semiconductors"];
    private const string TenantBSupplier = "Global Metals";

    /// <summary>Hostile text. Identical in the Java suite; no entry may equal a real entity, field or tenant.</summary>
    internal static readonly string[] ValuePayloadList =
    [
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
        new string('A', 10_000),
    ];

    private static readonly string[] Roles = ["customer", "analyst", "warehouse_operator", "supply_chain_manager"];

    // Theory data carries the index, not the text: NUL and lone control characters do not survive every test runner's
    // discovery and XML-report path.
    public static IEnumerable<object[]> ValuePayloads() => ValuePayloadList.Select((_, i) => new object[] { i });

    // ----- harness -------------------------------------------------------------------------

    private sealed record Outcome(int Status, string? Type, string? Message, List<Dictionary<string, object?>> Rows)
    {
        public bool Succeeded => Status == 200;
    }

    private static IntentCaller Caller(string tenant, string role) =>
        IntentCaller.Resolve(tenant, role, Service.KnownTenants);

    private static async Task<Outcome> Run(IntentCaller caller, string intentJson)
    {
        try
        {
            var result = await Service.ExecuteAsync(caller, intentJson);
            return new Outcome(200, null, null, (List<Dictionary<string, object?>>)result["rows"]!);
        }
        catch (Exception e)
        {
            var failure = SupplyChainIntentService.Classify(e);
            return new Outcome(failure.Status, failure.Type, failure.Message, []);
        }
    }

    private static Task<Outcome> Run(IntentCaller caller, JsonNode intent) => Run(caller, intent.ToJsonString());

    private static JsonObject Intent(string root, params string[] fields)
    {
        var selections = new JsonArray();
        foreach (var field in fields) selections.Add(new JsonObject { ["field"] = field });
        return new JsonObject { ["rootEntity"] = root, ["selections"] = selections };
    }

    private static JsonObject FieldFilter(string field, string op, JsonNode? value) =>
        new() { ["kind"] = "field", ["field"] = field, ["operator"] = op, ["value"] = value };

    private static JsonObject WithFilter(JsonObject intent, JsonNode filter)
    {
        intent["filter"] = filter;
        return intent;
    }

    private static List<string> Names(Outcome outcome, string column) =>
        outcome.Rows.Select(r => Convert.ToString(r[column]) ?? "").ToList();

    /// <summary>What an unauthenticated attacker must never learn from an error.</summary>
    private static void AssertNoInternalsLeak(string label, Outcome outcome)
    {
        if (outcome.Message is null) return;
        foreach (var marker in new[]
                 {
                     "\n   at ", "\n\tat ", "Npgsql", "org.postgresql", "System.", "Microsoft.", "EntityId[", TenantBSupplier
                 })
        {
            Assert.False(outcome.Message.Contains(marker, StringComparison.Ordinal),
                $"{label}: error message leaked '{marker}'");
        }
    }

    /// <summary>Never 500, never a leak; either a classified refusal or a clean success.</summary>
    private static void AssertClassified(string label, Outcome outcome)
    {
        Assert.False(outcome.Status == 500, $"{label}: unclassified failure {outcome.Message}");
        Assert.True(outcome.Succeeded || outcome.Status is 400 or 403, $"{label}: unexpected status {outcome.Status}");
        AssertNoInternalsLeak(label, outcome);
    }

    private static void AssertFailsClosed(string label, Outcome outcome)
    {
        Assert.False(outcome.Succeeded, $"{label}: hostile input was accepted");
        Assert.True(outcome.Status is 400 or 403,
            $"{label}: expected 400/403 but got {outcome.Status} {outcome.Message}");
        AssertNoInternalsLeak(label, outcome);
    }

    private static string Label(string slot, string payload)
    {
        var shown = payload.Length > 40 ? $"{payload[..40]}...({payload.Length})" : payload;
        return $"{slot} <- {shown.Replace("\u0000", "\\0")}";
    }

    // ----- sanity: the harness can actually see an attack succeed -----------------------------

    [Fact]
    public async Task Harness_sanity_filter_bites_and_tenant_a_is_isolated_from_b()
    {
        var analyst = Caller("tenant-a", "analyst");

        var hit = await Run(analyst, WithFilter(Intent("Product", "Sku"), FieldFilter("Category", "Eq", "Electronics")));
        Assert.True(hit.Succeeded, hit.Message);
        Assert.NotEmpty(hit.Rows);

        var miss = await Run(analyst,
            WithFilter(Intent("Product", "Sku"), FieldFilter("Category", "Eq", "no-such-category")));
        Assert.True(miss.Succeeded);
        Assert.Empty(miss.Rows);

        var all = await Run(analyst, Intent("Supplier", "Name"));
        Assert.Equal(TenantASuppliers, Names(all, "Supplier.Name").ToHashSet());
    }

    // ----- values stay data -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ValuePayloads))]
    public async Task String_filter_value_is_data_not_query(int payloadIndex)
    {
        var payload = ValuePayloadList[payloadIndex];
        var outcome = await Run(Caller("tenant-a", "analyst"),
            WithFilter(Intent("Product", "Sku"), FieldFilter("Category", "Eq", JsonValue.Create(payload))));

        var label = Label("Product.Category Eq", payload);
        AssertClassified(label, outcome);
        if (outcome.Succeeded)
            Assert.True(outcome.Rows.Count == 0,
                $"{label}: matched {outcome.Rows.Count} rows; the value acted like a pattern or query");
    }

    [Theory]
    [MemberData(nameof(ValuePayloads))]
    public async Task Tenant_boundary_holds_for_Eq_payloads(int payloadIndex)
    {
        var payload = ValuePayloadList[payloadIndex];
        var outcome = await Run(Caller("tenant-a", "analyst"),
            WithFilter(Intent("Supplier", "Name"), FieldFilter("Name", "Eq", JsonValue.Create(payload))));

        var label = Label("Supplier.Name Eq", payload);
        AssertClassified(label, outcome);
        if (outcome.Succeeded)
        {
            var seen = Names(outcome, "Supplier.Name");
            Assert.DoesNotContain(TenantBSupplier, seen);
            Assert.True(seen.All(TenantASuppliers.Contains), $"{label}: unexpected rows {string.Join(",", seen)}");
            Assert.True(seen.Count < TenantASuppliers.Count, $"{label}: a tautology returned every supplier");
        }
    }

    [Theory]
    [MemberData(nameof(ValuePayloads))]
    public async Task Tautology_via_OR_never_escapes_the_tenant_predicate(int payloadIndex)
    {
        var payload = ValuePayloadList[payloadIndex];
        // (Name = payload) OR (Name <> payload) is true for every row the caller may see, which is exactly
        // the tenant's rows. Anything extra means the OR was able to override the policy.
        var either = new JsonObject
        {
            ["kind"] = "or",
            ["expressions"] = new JsonArray(
                FieldFilter("Name", "Eq", JsonValue.Create(payload)),
                FieldFilter("Name", "Neq", JsonValue.Create(payload)))
        };

        var outcome = await Run(Caller("tenant-a", "analyst"), WithFilter(Intent("Supplier", "Name"), either));

        var label = Label("Supplier.Name OR tautology", payload);
        AssertClassified(label, outcome);
        if (outcome.Succeeded)
            Assert.True(TenantASuppliers.SetEquals(Names(outcome, "Supplier.Name")),
                $"{label}: result differs from the caller's tenant");
    }

    [Theory]
    [MemberData(nameof(ValuePayloads))]
    public async Task Denied_role_stays_denied_whatever_the_value(int payloadIndex)
    {
        var payload = ValuePayloadList[payloadIndex];
        // CUSTOMER may not read Supplier at all (see CanonicalIntentEndpointTests).
        var outcome = await Run(Caller("tenant-a", "customer"),
            WithFilter(Intent("Supplier", "Name"), FieldFilter("Name", "Eq", JsonValue.Create(payload))));

        var label = Label("customer Supplier Eq", payload);
        Assert.False(outcome.Succeeded, $"{label}: a denial was turned into success");
        Assert.Equal(403, outcome.Status);
        AssertNoInternalsLeak(label, outcome);
    }

    // ----- names are resolved, never interpreted ------------------------------------------------

    [Theory]
    [MemberData(nameof(ValuePayloads))]
    public async Task Hostile_names_are_refused_in_every_slot(int payloadIndex)
    {
        var payload = ValuePayloadList[payloadIndex];
        var analyst = Caller("tenant-a", "analyst");

        var rootEntity = new JsonObject
        {
            ["rootEntity"] = payload,
            ["selections"] = new JsonArray(new JsonObject { ["field"] = "Name" })
        };

        var selectionField = Intent("Supplier", payload);

        var relationship = Intent("Product", "Name");
        ((JsonArray)relationship["selections"]!).Add(new JsonObject
        {
            ["relationship"] = payload,
            ["children"] = new JsonArray(new JsonObject { ["field"] = "Status" })
        });

        var filterField = WithFilter(Intent("Supplier", "Name"), FieldFilter(payload, "Eq", JsonValue.Create("x")));

        var orderField = Intent("Supplier", "Name");
        orderField["order"] = new JsonArray(new JsonObject { ["field"] = payload, ["direction"] = "Asc" });

        AssertFailsClosed(Label("rootEntity", payload), await Run(analyst, rootEntity));
        AssertFailsClosed(Label("selection.field", payload), await Run(analyst, selectionField));
        AssertFailsClosed(Label("selection.relationship", payload), await Run(analyst, relationship));
        AssertFailsClosed(Label("filter.field", payload), await Run(analyst, filterField));
        AssertFailsClosed(Label("order.field", payload), await Run(analyst, orderField));
    }

    [Theory]
    [MemberData(nameof(ValuePayloads))]
    public async Task Hostile_operator_direction_and_kind_are_refused(int payloadIndex)
    {
        var payload = ValuePayloadList[payloadIndex];
        var analyst = Caller("tenant-a", "analyst");

        var badOperator = WithFilter(Intent("Supplier", "Name"), FieldFilter("Name", payload, JsonValue.Create("x")));

        var badDirection = Intent("Supplier", "Name");
        badDirection["order"] = new JsonArray(new JsonObject { ["field"] = "Name", ["direction"] = payload });

        var badKind = WithFilter(Intent("Supplier", "Name"), FieldFilter("Name", "Eq", JsonValue.Create("x")));
        badKind["filter"]!["kind"] = payload;

        AssertFailsClosed(Label("filter.operator", payload), await Run(analyst, badOperator));
        AssertFailsClosed(Label("order.direction", payload), await Run(analyst, badDirection));
        AssertFailsClosed(Label("filter.kind", payload), await Run(analyst, badKind));
    }

    // ----- identity never comes from the body ------------------------------------------------------

    [Fact]
    public async Task Identity_shaped_properties_are_rejected_at_root_and_in_filters()
    {
        var analyst = Caller("tenant-a", "analyst");
        foreach (var key in new[] { "tenant", "tenantId", "role", "actor", "isAdmin", "permissions", "scope" })
        {
            var atRoot = Intent("Supplier", "Name");
            atRoot[key] = "tenant-b";
            var root = await Run(analyst, atRoot);
            Assert.True(root.Status == 400, $"root.{key}: got {root.Status}");
            Assert.Equal("invalid_intent", root.Type);

            var inFilter = WithFilter(Intent("Supplier", "Name"), FieldFilter("Name", "Eq", JsonValue.Create("x")));
            inFilter["filter"]![key] = "tenant-b";
            AssertFailsClosed($"filter.{key}", await Run(analyst, inFilter));
        }
    }

    [Theory]
    [MemberData(nameof(ValuePayloads))]
    public void Hostile_identity_headers_are_refused(int payloadIndex)
    {
        var payload = ValuePayloadList[payloadIndex];
        // "tenant-b" is a real tenant, so it is a legitimate header value, not an injection.
        if (payload == "tenant-b") return;
        var known = Service.KnownTenants;
        Assert.Throws<SecurityException>(() => IntentCaller.Resolve(payload, "analyst", known));
        Assert.Throws<SecurityException>(() => IntentCaller.Resolve("tenant-a", payload, known));
    }

    // ----- structural abuse and resource limits fail closed ------------------------------------------

    private const string OneField = """{"kind":"field","field":"Name","operator":"Eq","value":"x"}""";

    private static readonly Lazy<Dictionary<string, string>> MalformedIntentSet = new(() =>
    {
        const int depth = 500;
        var deep = new StringBuilder();
        for (var i = 0; i < depth; i++) deep.Append("""{"kind":"and","expressions":[""");
        deep.Append(OneField);
        for (var i = 0; i < depth; i++) deep.Append("]}");

        var wide = new StringBuilder("""{"kind":"or","expressions":[""");
        for (var i = 0; i < 20_000; i++)
        {
            if (i > 0) wide.Append(',');
            wide.Append(OneField);
        }

        wide.Append("]}");

        var many = new StringBuilder();
        for (var i = 0; i < 50_000; i++)
        {
            if (i > 0) many.Append(',');
            many.Append("""{"field":"Name"}""");
        }

        const string head = """{"rootEntity":"Supplier","selections":[{"field":"Name"}]""";
        return new Dictionary<string, string>
        {
            ["empty"] = "",
            ["whitespace"] = "   ",
            ["truncated-object"] = "{",
            ["json-null"] = "null",
            ["json-array"] = "[]",
            ["json-number"] = "42",
            ["json-string"] = "\"Supplier\"",
            ["empty-object"] = "{}",
            ["null-root"] = """{"rootEntity":null,"selections":[{"field":"Name"}]}""",
            ["selections-not-array"] = """{"rootEntity":"Supplier","selections":"Name"}""",
            ["negative-limit"] = head + ""","limit":-1}""",
            ["negative-offset"] = head + ""","offset":-1}""",
            ["filter-depth-bomb"] = head + ""","filter":""" + deep + "}",
            ["filter-node-bomb"] = head + ""","filter":""" + wide + "}",
            ["selection-bomb"] = """{"rootEntity":"Supplier","selections":[""" + many + "]}",
            ["unknown-property"] = head + ""","unexpected":{"a":1}}""",
        };
    });

    public static IEnumerable<object[]> MalformedIntentNames() =>
        MalformedIntentSet.Value.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(MalformedIntentNames))]
    public async Task Malformed_and_oversized_intents_fail_closed(string name) =>
        AssertFailsClosed($"intent {name}", await Run(Caller("tenant-a", "analyst"), MalformedIntentSet.Value[name]));

    // ----- policy side channels ------------------------------------------------------------------

    [Fact]
    public async Task Filtering_on_a_field_the_caller_cannot_read_does_not_leak_its_value()
    {
        // WAREHOUSE_OPERATOR cannot read Supplier.RiskScore. If it could still *filter* on it, a hit and a miss
        // would differ and the value could be recovered by guessing. C# read filters support Eq, Neq and In only,
        // so the probes use Eq: an unsupported operator is refused by the parser and would prove nothing.
        static JsonObject Probe(decimal score) =>
            WithFilter(Intent("Supplier", "Name"), FieldFilter("RiskScore", "Eq", JsonValue.Create(score)));

        // Positive control: for a role that may read the field, the same probes really do differ.
        var analyst = Caller("tenant-a", "analyst");
        Assert.Single((await Run(analyst, Probe(0.87m))).Rows);
        Assert.Empty((await Run(analyst, Probe(0.5m))).Rows);

        var op = Caller("tenant-a", "warehouse_operator");
        foreach (var score in new[] { 0.87m, 0.5m })
        {
            var outcome = await Run(op, Probe(score));
            Assert.True(outcome.Status == 403, $"operator RiskScore Eq {score}: got {outcome.Status} {outcome.Message}");
        }
    }

    [Fact]
    public async Task Every_role_stays_inside_its_tenant_and_never_sees_another_tenants_suppliers()
    {
        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
        {
            var own = tenant == "tenant-a" ? TenantASuppliers : new HashSet<string> { TenantBSupplier };
            foreach (var role in Roles)
            {
                var outcome = await Run(Caller(tenant, role), Intent("Supplier", "Name"));
                var label = $"{tenant}/{role}";
                AssertClassified(label, outcome);
                if (outcome.Succeeded)
                    Assert.True(Names(outcome, "Supplier.Name").All(own.Contains),
                        $"{label}: rows outside the caller's tenant");
            }
        }
    }

    // ----- nothing the corpus does is remembered -----------------------------------------------------

    [Fact]
    public async Task Canary_reads_are_unchanged_after_the_whole_corpus_has_run()
    {
        var analyst = Caller("tenant-a", "analyst");
        var before = await Run(analyst, Intent("Supplier", "Name", "RiskScore"));
        var productsBefore = await Run(analyst, Intent("Product", "Sku"));
        var customerBefore = await Run(Caller("tenant-a", "customer"), Intent("Supplier", "Name"));

        foreach (var payload in ValuePayloadList)
        {
            await Run(analyst, WithFilter(Intent("Supplier", "Name"), FieldFilter("Name", "Eq", JsonValue.Create(payload))));
            await Run(analyst, WithFilter(Intent("Product", "Sku"), FieldFilter("Category", "Eq", JsonValue.Create(payload))));
            await Run(analyst, Intent("Supplier", payload));
        }

        foreach (var raw in MalformedIntentSet.Value.Values) await Run(analyst, raw);

        Assert.Equal(before.Rows, (await Run(analyst, Intent("Supplier", "Name", "RiskScore"))).Rows);
        Assert.Equal(productsBefore.Rows, (await Run(analyst, Intent("Product", "Sku"))).Rows);
        var customerAfter = await Run(Caller("tenant-a", "customer"), Intent("Supplier", "Name"));
        Assert.Equal(customerBefore.Status, customerAfter.Status);
        Assert.Equal(403, customerAfter.Status);
    }
}
