using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Foundgine.Runtime;
using HotChocolate;
using HotChocolate.Types;

namespace Foundgine.Extensions.GraphQL.HotChocolate;

/// <summary>
/// Reusable Hot Chocolate query type exposing the application's canonical open-intent handler.
/// The caller principal is obtained from a host-owned principal provider, never from GraphQL arguments.
/// </summary>
public sealed class FoundgineIntentGraphQLResolver
{
    public const int MaxIntentBytes = 65_536;

    // The same web defaults as the REST and MCP adapters, so one handler result has one JSON shape.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Expose the shared application intent handler as the GraphQL <c>foundgineIntent</c> field.</summary>
    [GraphQLName("foundgineIntent")]
    [GraphQLType(typeof(AnyType))]
    public async Task<JsonElement> FoundgineIntent(
        [Service] IFoundgineIntentHandler handler,
        [Service] IFoundgineIntentPrincipalProvider principalProvider,
        [GraphQLType(typeof(AnyType))] JsonElement? intent = null,
        string mode = "execute",
        CancellationToken cancellationToken = default)
    {
        var caller = principalProvider.GetCurrentPrincipal();
        if (caller is null || caller.Identity?.IsAuthenticated != true)
            throw GraphQLError("unauthenticated", "An authenticated caller is required.");

        string? intentJson = null;
        if (intent is JsonElement value && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            intentJson = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

        if (intentJson is not null && Encoding.UTF8.GetByteCount(intentJson) > MaxIntentBytes)
            throw GraphQLError("invalid_intent", $"An intent may not exceed {MaxIntentBytes} bytes.");

        var result = await ExecuteAsync(handler, caller, intentJson, mode, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw GraphQLError(result.ErrorCode ?? "intent_failed", result.ErrorMessage ?? "The intent could not be completed.");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, JsonOptions));
        return document.RootElement.Clone();
    }

    /// <summary>Protocol-neutral helper shared by GraphQL tests and host-specific GraphQL resolvers.</summary>
    [GraphQLIgnore]
    public static async Task<FoundgineIntentResult> ExecuteAsync(
        IFoundgineIntentHandler handler,
        ClaimsPrincipal caller,
        string? intentJson,
        string mode = "execute",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Identity?.IsAuthenticated != true)
            return FoundgineIntentResult.Failure(FoundgineIntentOutcome.Unauthenticated, "unauthenticated", "An authenticated caller is required.");

        var operation = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "execute" => FoundgineIntentOperation.Execute,
            "explain" => FoundgineIntentOperation.Explain,
            "capabilities" => FoundgineIntentOperation.Capabilities,
            _ => throw GraphQLError("invalid_intent", "Mode must be execute, explain or capabilities.")
        };

        if (operation != FoundgineIntentOperation.Capabilities && string.IsNullOrWhiteSpace(intentJson))
            return FoundgineIntentResult.Failure(FoundgineIntentOutcome.InvalidIntent, "invalid_intent", "An intent is required for execute and explain operations.");
        if (intentJson is not null && Encoding.UTF8.GetByteCount(intentJson) > MaxIntentBytes)
            return FoundgineIntentResult.Failure(FoundgineIntentOutcome.InvalidIntent, "invalid_intent", $"An intent may not exceed {MaxIntentBytes} bytes.");

        return await handler.HandleAsync(
            new FoundgineIntentRequest(operation, operation == FoundgineIntentOperation.Capabilities ? null : intentJson),
            caller,
            cancellationToken).ConfigureAwait(false);
    }

    private static GraphQLException GraphQLError(string code, string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).Build());
}
