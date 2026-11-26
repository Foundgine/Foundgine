using System.Security.Claims;
using Foundgine.Extensions.GraphQL.HotChocolate;
using Foundgine.SupplyChain.Advanced.Data;
using Foundgine.SupplyChain.Advanced.Intent;
using Xunit;

namespace Foundgine.SupplyChain.Advanced.Tests;

public sealed class GraphQlIntentAdapterTests
{
    [Fact]
    public async Task Graphql_adapter_executes_the_same_canonical_intent_as_rest_and_mcp()
    {
        var service = new SupplyChainIntentService(SupplyChainData.Seed());
        var handler = new SupplyChainIntentHandler(service);
        var caller = Principal("tenant-a", "analyst");
        const string intent = """{"rootEntity":"Supplier","selections":[{"field":"Name"},{"field":"RiskScore"}]}""";

        var result = await FoundgineIntentGraphQLResolver.ExecuteAsync(handler, caller, intent);

        Assert.True(result.Succeeded);
        var data = (Dictionary<string, object?>)result.Data!;
        Assert.Equal(2, data["rowCount"]);
        Assert.Contains("Supplier.RiskScore", ((List<Dictionary<string, object?>>)data["rows"]!)[0].Keys);
        Assert.Equal("tenant-a", ((Dictionary<string, object?>)data["caller"]!)["tenant"]);
    }

    [Fact]
    public async Task Graphql_adapter_uses_host_identity_not_identity_fields_in_the_intent()
    {
        var service = new SupplyChainIntentService(SupplyChainData.Seed());
        var handler = new SupplyChainIntentHandler(service);
        var caller = Principal("tenant-a", "analyst");
        const string intent = """{"rootEntity":"Supplier","selections":[{"field":"Name"}],"tenant":"tenant-b"}""";

        var result = await FoundgineIntentGraphQLResolver.ExecuteAsync(handler, caller, intent);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_intent", result.ErrorCode);
        Assert.Contains("tenant", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private static ClaimsPrincipal Principal(string tenant, string role) => new(
        new ClaimsIdentity(
            [new Claim(IntentCaller.TenantClaim, tenant), new Claim(ClaimTypes.Role, role)],
            "GraphQlTestAuthentication"));
}
