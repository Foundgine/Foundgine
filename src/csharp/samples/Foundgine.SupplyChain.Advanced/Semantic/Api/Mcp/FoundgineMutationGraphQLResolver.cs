using System.Text;
using System.Text.Json;
using Foundgine.Runtime;
using HotChocolate;
using HotChocolate.Types;

namespace Foundgine.SupplyChain.Advanced.Mutation;

/// <summary>GraphQL adapter for the exact same dispatcher used by MCP.</summary>
public sealed class FoundgineMutationGraphQLResolver
{
    [GraphQLName("foundgineMutation")]
    [GraphQLType(typeof(AnyType))]
    public async Task<JsonElement> FoundgineMutation(
        [Service] SupplyChainMutationDispatcher dispatcher,
        [Service] IFoundgineIntentPrincipalProvider principalProvider,
        [GraphQLType(typeof(AnyType))] JsonElement mutation,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var principal = principalProvider.GetCurrentPrincipal();
        if (principal?.Identity?.IsAuthenticated != true) throw Error("unauthenticated", "An authenticated caller is required.");
        var json = mutation.ValueKind == JsonValueKind.String ? mutation.GetString()! : mutation.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > SupplyChainMutationDispatcher.MaxMutationBytes) throw Error("invalid_mutation", $"A mutation may not exceed {SupplyChainMutationDispatcher.MaxMutationBytes} bytes.");
        var result = await dispatcher.DispatchJsonAsync(json, principal, correlationId, cancellationToken).ConfigureAwait(false);
        if (result.Status is "denied" or "rejected") throw Error(result.ErrorCode ?? "mutation_failed", result.ErrorMessage ?? "The mutation could not be completed.");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    private static GraphQLException Error(string code, string message) => new(ErrorBuilder.New().SetCode(code).SetMessage(message).Build());
}
