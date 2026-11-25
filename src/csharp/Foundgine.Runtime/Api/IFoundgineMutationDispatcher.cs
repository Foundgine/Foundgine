using System.Security.Claims;

namespace Foundgine.Runtime;

/// <summary>
/// Application-owned translation of a mutation command body into the runtime mutation lifecycle
/// (execute, dry run, approve, execute approved). The HTTP adapter hands it the raw body; the
/// application decides which commands exist, resolves approval handles from server-side storage and
/// derives actor and tenant from <paramref>
///     <name>caller</name>
/// </paramref>
/// , never from the body.
/// </summary>
public interface IFoundgineMutationDispatcher
{
    Task<FoundgineIntentResult> DispatchAsync(
        string mutationJson,
        ClaimsPrincipal caller,
        string? correlationId,
        CancellationToken cancellationToken = default);
}
