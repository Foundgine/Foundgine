using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Foundgine.SupplyChain.Advanced.Intent;

/// <summary>
/// Development-only bridge for the sample's X-Foundgine-* demonstration headers.
/// Production hosts must populate HttpContext.User through real authentication middleware.
/// </summary>
public sealed class SupplyChainIntentDemoIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var claims = new List<Claim>();
            var tenant = context.Request.Headers[IntentCaller.TenantHeader].FirstOrDefault();
            var role = context.Request.Headers[IntentCaller.RoleHeader].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(tenant))
                claims.Add(new Claim(IntentCaller.TenantClaim, tenant));
            if (!string.IsNullOrWhiteSpace(role))
                claims.Add(new Claim(ClaimTypes.Role, role));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "FoundgineDemoHeaders"));
        }

        await next(context).ConfigureAwait(false);
    }
}
