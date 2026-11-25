using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Foundgine.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Foundgine.Providers.Tools.MCP;

/// <summary>
/// Generic MCP tool adapter for an application's open-intent handler. It contains no
/// domain-specific service, identity policy, semantic model or authorization rules.
/// </summary>
[McpServerToolType]
public sealed class FoundgineIntentMcpTools(
    IFoundgineIntentHandler handler,
    IFoundgineIntentPrincipalProvider principalProvider)
{
    public const int MaxIntentBytes = 65_536;

    [McpServerTool(Name = "foundgine_intent")]
    [Description(
        "Submit an open Foundgine semantic intent. Supply a provider-neutral JSON intent and choose execute, explain or capabilities. " +
        "The authenticated caller is supplied by the host and must never be included in the arguments.")]
    public async Task<CallToolResult> Intent(
        JsonElement? intent = null,
        string mode = "execute",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var principal = principalProvider.GetCurrentPrincipal();
            if (principal is null || principal.Identity?.IsAuthenticated != true)
                return Error("unauthenticated: An authenticated caller is required.");

            // A client may send "mode": null or "" to mean the default.
            FoundgineIntentOperation? operation = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "" or "execute" => FoundgineIntentOperation.Execute,
                "explain" => FoundgineIntentOperation.Explain,
                "capabilities" => FoundgineIntentOperation.Capabilities,
                _ => null
            };
            if (operation is null)
                return Error("invalid_intent: Mode must be execute, explain or capabilities.");

            string? intentJson = null;
            if (operation != FoundgineIntentOperation.Capabilities && !TryGetIntentJson(intent, out intentJson, out var problem))
                return Error($"invalid_intent: {problem}");

            var result = await handler.HandleAsync(
                new FoundgineIntentRequest(operation.Value, intentJson),
                principal,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
                return Error($"{result.ErrorCode ?? "intent_failed"}: {result.ErrorMessage ?? "The intent could not be completed."}");

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Data, JsonOptions) }]
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The handler classifies its own failures; anything that escapes it is never described to the client.
            return Error("internal_error: The intent could not be completed.");
        }
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static bool TryGetIntentJson(JsonElement? intent, out string? json, out string? problem)
    {
        json = null;
        problem = null;
        if (intent is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            problem = "Missing 'intent'.";
            return false;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > MaxIntentBytes)
        {
            problem = $"An intent may not exceed {MaxIntentBytes} bytes.";
            return false;
        }

        json = text;
        return true;
    }
}
