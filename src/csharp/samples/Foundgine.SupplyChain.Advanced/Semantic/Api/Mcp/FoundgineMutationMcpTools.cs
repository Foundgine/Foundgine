using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Foundgine.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Foundgine.SupplyChain.Advanced.Mutation;

/// <summary>MCP transport adapter; all authorization, approval and execution stays in the application dispatcher.</summary>
[McpServerToolType]
public sealed class FoundgineMutationMcpTools(
    SupplyChainMutationDispatcher dispatcher,
    IFoundgineIntentPrincipalProvider principalProvider)
{
    public const int MaxMutationBytes = 65_536;
    [McpServerTool(Name = "foundgine_mutation")]
    [Description("Run a Supply Chain mutation using mode dry_run, approve, execute or execute_approved. Caller identity is supplied by the authenticated host, never by tool arguments.")]
    public async Task<CallToolResult> Mutation(JsonElement mutation, string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var principal = principalProvider.GetCurrentPrincipal();
        if (principal?.Identity?.IsAuthenticated != true) return Error("unauthenticated: An authenticated caller is required.");
        var json = mutation.ValueKind == JsonValueKind.String ? mutation.GetString()! : mutation.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > SupplyChainMutationDispatcher.MaxMutationBytes)
            return Error($"invalid_mutation: A mutation may not exceed {SupplyChainMutationDispatcher.MaxMutationBytes} bytes.");
        var result = await dispatcher.DispatchJsonAsync(json, principal, correlationId, cancellationToken).ConfigureAwait(false);
        return result.Status is "denied" or "rejected" ? Error($"{result.ErrorCode}: {result.ErrorMessage}") :
            new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }] };
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
