using System.Security.Claims;

namespace Foundgine.Runtime;

/// <summary>
/// Host-owned source of the authenticated principal for a protocol invocation.
/// HTTP, GraphQL, MCP session and non-HTTP hosts can provide their own implementation.
/// A missing or unauthenticated principal must cause the adapter to fail closed.
/// </summary>
public interface IFoundgineIntentPrincipalProvider
{
    ClaimsPrincipal? GetCurrentPrincipal();
}
