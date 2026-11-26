using System.Security.Claims;
using System.Text.Json;
using Foundgine.Runtime;
using Foundgine.SupplyChain.Advanced.Intent;
using Foundgine.SupplyChain.Advanced.Mutation;
using Xunit;

namespace Foundgine.SupplyChain.Advanced.Tests;

public sealed class MutationTransportAdapterTests
{
    private sealed class PrincipalProvider(ClaimsPrincipal principal) : IFoundgineIntentPrincipalProvider
    {
        public ClaimsPrincipal? GetCurrentPrincipal() => principal;
    }

    private static ClaimsPrincipal Caller() => new(new ClaimsIdentity([
        new Claim("sub", "alice"), new Claim(IntentCaller.TenantClaim, "tenant-a"),
        new Claim(ClaimTypes.Role, "Customer")
    ], "adapter-test"));

    [Fact]
    public async Task Graphql_and_mcp_adapters_share_the_same_mutation_dispatcher()
    {
        var dispatcher = new SupplyChainMutationDispatcher();
        var provider = new PrincipalProvider(Caller());
        var graphql = new FoundgineMutationGraphQLResolver();
        var mcp = new FoundgineMutationMcpTools(dispatcher, provider);
        using var graphQlPayload = JsonDocument.Parse("""{"mode":"dry_run","tool":"place_order","customerId":1,"lines":[{"productId":4,"quantity":1}],"idempotencyKey":"adapter-place"}""");
        using var mcpPayload = JsonDocument.Parse("""{"mode":"dry_run","tool":"place_order","customerId":1,"lines":[{"productId":4,"quantity":1}],"idempotencyKey":"adapter-place"}""");

        var graphQlResult = await graphql.FoundgineMutation(dispatcher, provider, graphQlPayload.RootElement, "graphql-trace");
        Assert.Equal("dry_run", graphQlResult.GetProperty("status").GetString());
        var mcpResult = await mcp.Mutation(mcpPayload.RootElement, "mcp-trace");
        Assert.False(mcpResult.IsError);
        Assert.Contains(dispatcher.Audit, x => x.CorrelationId == "graphql-trace");
        Assert.Contains(dispatcher.Audit, x => x.CorrelationId == "mcp-trace");
    }
}
