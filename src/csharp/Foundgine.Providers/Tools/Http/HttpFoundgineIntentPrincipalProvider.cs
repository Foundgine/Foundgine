using System.Security.Claims;
using Foundgine.Runtime;
using Microsoft.AspNetCore.Http;

namespace Foundgine.Providers.Tools.Http;

/// <summary>Resolves the current authenticated principal from ASP.NET Core's request context.</summary>
public sealed class HttpFoundgineIntentPrincipalProvider(IHttpContextAccessor accessor)
    : IFoundgineIntentPrincipalProvider
{
    public ClaimsPrincipal? GetCurrentPrincipal() => accessor.HttpContext?.User;
}
