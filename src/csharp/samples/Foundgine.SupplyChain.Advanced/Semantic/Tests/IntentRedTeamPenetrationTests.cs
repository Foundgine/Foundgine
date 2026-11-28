using System.Security;
using System.Text.Json.Nodes;
using Foundgine.SupplyChain.Advanced.Data;
using Foundgine.SupplyChain.Advanced.Intent;
using Xunit;

namespace Foundgine.SupplyChain.Advanced.Tests;

/// <summary>
/// Red-team penetration tests for the intent boundary. <see cref="IntentInjectionCorpusTests"/> asks whether hostile
/// <em>text</em> can change a query. These tests play an authenticated, low-privilege caller who sends well-formed
/// requests and tries to learn or reach something the policy denies:
/// <list type="bullet">
///   <item>inference through filters and ordering on unreadable fields and relationships;</item>
///   <item>wrong JSON types and serializer metadata keys;</item>
///   <item>parser errors that disclose implementation details;</item>
///   <item>ambiguous identity headers;</item>
///   <item>cross-caller bleed under concurrency.</item>
/// </list>
/// Every inference probe has a positive control showing that it does return information to an allowed role.
/// They run in process against the sample's in-memory provider, not over HTTP or against a database.
/// Mirrors the Java <c>IntentRedTeamPenetrationTest</c>.
/// </summary>
public sealed class IntentRedTeamPenetrationTests
{
    private static readonly SupplyChainIntentService Service = new(SupplyChainData.Seed());

    private const string Head = """{"rootEntity":"Supplier","selections":[{"field":"Name"}]""";
    private const string SupplierNames = Head + "}";

    private sealed record Outcome(int Status, string? Message, List<string> Names)
    {
        public override string ToString() => $"{Status} {Message} [{string.Join(", ", Names)}]";
    }

    private static IntentCaller Caller(string tenant, string role) =>
        IntentCaller.Resolve(tenant, role, Service.KnownTenants);

    private static async Task<Outcome> Run(IntentCaller caller, string intent)
    {
        try
        {
            var result = await Service.ExecuteAsync(caller, intent);
            var rows = (List<Dictionary<string, object?>>)result["rows"]!;
            return new Outcome(200, null,
                rows.Select(r => r.TryGetValue("Supplier.Name", out var n) ? Convert.ToString(n)! : "").ToList());
        }
        catch (Exception e)
        {
            var failure = SupplyChainIntentService.Classify(e);
            return new Outcome(failure.Status, failure.Message, []);
        }
    }

    private static Task<Outcome> Operator(string intent) => Run(Caller("tenant-a", "warehouse_operator"), intent);

    private static Task<Outcome> Analyst(string intent) => Run(Caller("tenant-a", "analyst"), intent);

    private static string Filtered(string filter) => Head + ",\"filter\":" + filter + "}";

    private static string Ordered(string direction) =>
        Head + ",\"order\":[{\"field\":\"RiskScore\",\"direction\":\"" + direction + "\"}]}";

    private static string RiskEquals(string value) =>
        """{"kind":"field","field":"RiskScore","operator":"Eq","value":""" + value + "}";

    private const string NameIsKiwi =
        """{"kind":"field","field":"Name","operator":"Eq","value":"Kiwi Components"}""";

    private const string CriticalIncident =
        """{"kind":"relationship","relationship":"incidents","quantifier":"Some","predicate":{"kind":"field","field":"Severity","operator":"Eq","value":"Critical"}}""";

    private static void AssertForbidden(string label, Outcome outcome) =>
        Assert.True(outcome.Status == 403 && outcome.Names.Count == 0, $"{label}: expected 403, got {outcome}");

    private static void AssertRefused(string label, Outcome outcome) =>
        Assert.True(outcome.Status is 400 or 403, $"{label}: expected 400/403, got {outcome}");

    // ----- inference attacks on data the role cannot read ----------------------------------------

    [Fact]
    public async Task Hidden_field_cannot_be_recovered_by_filtering()
    {
        Assert.Equal(new List<string> { "Pacific Semiconductors" }, (await Analyst(Filtered(RiskEquals("0.87")))).Names);
        Assert.Empty((await Analyst(Filtered(RiskEquals("0.5")))).Names);

        AssertForbidden("Eq hit", await Operator(Filtered(RiskEquals("0.87"))));
        AssertForbidden("Eq miss", await Operator(Filtered(RiskEquals("0.5"))));
        AssertForbidden("Neq", await Operator(Filtered(RiskEquals("0.87").Replace("\"Eq\"", "\"Neq\""))));
        AssertForbidden("In", await Operator(Filtered(RiskEquals("[0.87,0.5]").Replace("\"Eq\"", "\"In\""))));
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    public async Task Hidden_field_stays_hidden_when_nested_inside_a_boolean_filter(string kind)
    {
        var nested = "{\"kind\":\"" + kind + "\",\"expressions\":[" + NameIsKiwi +
                     ",{\"kind\":\"and\",\"expressions\":[" + RiskEquals("0.87") + "]}]}";
        AssertForbidden(kind, await Operator(Filtered(nested)));
    }

    [Theory]
    [InlineData("Asc")]
    [InlineData("Desc")]
    public async Task Hidden_field_cannot_be_ranked_by_ordering(string direction)
    {
        // Positive control: the analyst's order really depends on the score.
        var expected = new List<string> { "Kiwi Components", "Pacific Semiconductors" };
        if (direction == "Desc") expected.Reverse();
        Assert.Equal(expected, (await Analyst(Ordered(direction))).Names);

        AssertForbidden("order " + direction, await Operator(Ordered(direction)));
    }

    [Theory]
    [InlineData("Some")]
    [InlineData("None")]
    [InlineData("All")]
    public async Task Hidden_relationship_cannot_be_probed_with_a_relationship_filter(string quantifier) =>
        AssertForbidden("incidents " + quantifier,
            await Operator(Filtered(CriticalIncident.Replace("\"Some\"", "\"" + quantifier + "\""))));

    [Fact]
    public async Task Explain_and_capabilities_do_not_open_a_way_round_the_policy()
    {
        var op = Caller("tenant-a", "warehouse_operator");
        foreach (var intent in new[] { Filtered(RiskEquals("0.87")), Ordered("Asc"), Filtered(CriticalIncident) })
        {
            var error = Record(() => Service.Explain(op, intent));
            Assert.True(error is not null, "explain accepted a query that execution refuses");
            Assert.Equal(403, SupplyChainIntentService.Classify(error!).Status);
        }

        var entities = (List<Dictionary<string, object?>>)Service.Capabilities(op)["entities"]!;
        var supplier = entities.Single(e => (string)e["entity"]! == "Supplier");
        var read = (Dictionary<string, object?>)supplier["read"]!;
        Assert.DoesNotContain("RiskScore", (List<string>)read["fields"]!);
        Assert.DoesNotContain("incidents", (List<string>)read["relationships"]!);

        Assert.Equal(403, (await Operator(Filtered(RiskEquals("0.87")))).Status);
    }

    private static Exception? Record(Func<object?> action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    // ----- type confusion and parser disclosure ------------------------------------------------------

    public static IEnumerable<object[]> WrongTypes() =>
    [
        ["limit as word", ",\"limit\":\"abc\"}"],
        ["limit as object", ",\"limit\":{}}"],
        ["limit as boolean", ",\"limit\":true}"],
        ["limit as fraction", ",\"limit\":-0.5}"],
        ["limit beyond int", ",\"limit\":4294967296}"],
        ["offset as array", ",\"offset\":[1]}"],
        ["order as string", ",\"order\":\"RiskScore\"}"],
        ["order as object", ",\"order\":{\"field\":\"Name\",\"direction\":\"Asc\"}}"],
        ["order entry as string", ",\"order\":[\"Name\"]}"],
        ["order path as string", ",\"order\":[{\"field\":\"Name\",\"direction\":\"Asc\",\"relationshipPath\":\"x\"}]}"],
        ["order direction numeric", ",\"order\":[{\"field\":\"Name\",\"direction\":999}]}"],
        ["filter as string", ",\"filter\":\"Name = 'x'\"}"],
        ["filter as array", ",\"filter\":[]}"],
        ["operator numeric", ",\"filter\":{\"kind\":\"field\",\"field\":\"Name\",\"operator\":999,\"value\":\"x\"}}"],
        ["operator numeric text", ",\"filter\":{\"kind\":\"field\",\"field\":\"Name\",\"operator\":\"999\",\"value\":\"x\"}}"],
        ["field as object", ",\"filter\":{\"kind\":\"field\",\"field\":{},\"operator\":\"Eq\",\"value\":\"x\"}}"],
        ["kind as number", ",\"filter\":{\"kind\":1}}"],
        ["after as object", ",\"after\":{}}"],
        ["trailing document", "} {\"rootEntity\":\"Product\"}"],
    ];

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public async Task Wrong_json_types_are_refused_not_coerced_or_ignored(string label, string tail) =>
        AssertRefused(label, await Analyst(Head + tail));

    [Fact]
    public async Task Children_of_the_wrong_type_are_refused() =>
        AssertRefused("children as string", await Analyst(
            """{"rootEntity":"Supplier","selections":[{"field":"Name","children":"RiskScore"}]}"""));

    [Theory]
    [InlineData("__proto__")]
    [InlineData("constructor")]
    [InlineData("prototype")]
    [InlineData("$type")]
    [InlineData("@class")]
    [InlineData("$where")]
    [InlineData("tenantId ")]
    public async Task Serializer_metadata_keys_are_rejected_everywhere(string key)
    {
        var member = "\"" + key + "\":\"System.Diagnostics.Process\"";
        string[] intents =
        [
            Head + "," + member + "}",
            """{"rootEntity":"Supplier","selections":[{"field":"Name",""" + member + "}]}",
            Filtered(NameIsKiwi.TrimEnd('}') + "," + member + "}"),
            Head + ",\"order\":[{\"field\":\"Name\",\"direction\":\"Asc\"," + member + "}]}",
        ];
        foreach (var intent in intents) AssertRefused(key, await Analyst(intent));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"rootEntity\":}")]
    [InlineData("{CTRL}")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>")]
    [InlineData("rootEntity=Supplier")]
    [InlineData("{\"rootEntity\":\"Supplier\",\"selections\":[{\"field\":\"Name\"}],\"limit\":1e99999}")]
    public async Task Parser_errors_do_not_disclose_implementation_details(string body)
    {
        var outcome = await Analyst(body.Replace("{CTRL}", "\u0001\u0002"));
        AssertRefused(body, outcome);
        foreach (var marker in new[]
                 {
                     "System.", "Microsoft.", "Foundgine.", "com.fasterxml", "java.", "Source:", "BytePosition",
                     "LineNumber", "\n   at ", "root:x:"
                 })
            Assert.DoesNotContain(marker, outcome.Message ?? "");
    }

    // ----- identity ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("tenant-a", "analyst,supply_chain_manager")]
    [InlineData("tenant-a", "customer analyst")]
    [InlineData("tenant-a", "1")]
    [InlineData("tenant-a", "analyst\r\nX-Foundgine-Role: supply_chain_manager")]
    [InlineData("tenant-a", "analyst{NUL}")]
    [InlineData("tenant-a", "ａnalyst")]
    [InlineData("tenant-a,tenant-b", "analyst")]
    [InlineData("TENANT-B", "analyst")]
    [InlineData("tenant-a\r\ntenant-b", "analyst")]
    [InlineData("*", "analyst")]
    public void Ambiguous_identity_headers_never_resolve_to_a_caller(string tenant, string role)
    {
        // NUL is spelled as a token: raw control characters do not survive every test runner's report format.
        var hostileRole = role.Replace("{NUL}", "\0");
        Assert.Throws<SecurityException>(() => IntentCaller.Resolve(tenant, hostileRole, Service.KnownTenants));
    }

    [Theory]
    [InlineData("\"tenant\":\"tenant-b\",\"Tenant\":\"tenant-b\"")]
    [InlineData("\"TENANT\":\"tenant-b\"")]
    [InlineData("\"Role\":\"supply_chain_manager\"")]
    public async Task Identity_in_the_body_is_refused_in_any_casing(string member) =>
        AssertRefused(member, await Analyst(Head + "," + member + "}"));

    [Fact]
    public async Task Duplicate_root_entity_cannot_slip_past_a_denial()
    {
        var customer = Caller("tenant-a", "customer");
        var outcome = await Run(customer,
            """{"rootEntity":"Product","rootEntity":"Supplier","selections":[{"field":"Name"}]}""");
        AssertRefused("duplicate rootEntity", outcome);
        Assert.Empty(outcome.Names);
    }

    // ----- concurrency -----------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_callers_never_receive_each_others_results()
    {
        IntentCaller[] callers =
        [
            Caller("tenant-a", "analyst"), Caller("tenant-b", "analyst"),
            Caller("tenant-a", "warehouse_operator"), Caller("tenant-a", "customer")
        ];
        var expected = new List<Outcome>();
        foreach (var caller in callers) expected.Add(await Run(caller, SupplierNames));
        Assert.NotEqual(expected[0].Names, expected[1].Names);
        Assert.Equal(403, expected[3].Status);

        // A fresh service, so engine creation races too.
        var service = new SupplyChainIntentService(SupplyChainData.Seed());
        var results = await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(async () =>
        {
            try
            {
                var result = await service.ExecuteAsync(callers[i % 4], SupplierNames);
                var rows = (List<Dictionary<string, object?>>)result["rows"]!;
                return (i, Status: 200, Names: rows.Select(r => (string)r["Supplier.Name"]!).ToList());
            }
            catch (Exception e)
            {
                return (i, SupplyChainIntentService.Classify(e).Status, Names: new List<string>());
            }
        })));

        foreach (var (i, status, names) in results)
        {
            Assert.Equal(expected[i % 4].Status, status);
            Assert.Equal(expected[i % 4].Names, names);
        }
    }
}
