using Foundgine.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foundgine.Providers.Tools.Http;

/// <summary>
/// Registers the ASP.NET Core principal source used by the generic MCP and GraphQL intent adapters.
/// A principal provider that the host has already registered is kept.
/// </summary>
public static class FoundgineIntentHttpServiceCollectionExtensions
{
    public static IServiceCollection AddFoundgineIntentHttpPrincipal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IFoundgineIntentPrincipalProvider, HttpFoundgineIntentPrincipalProvider>();
        return services;
    }
}
