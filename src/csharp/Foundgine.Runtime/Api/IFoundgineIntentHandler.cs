using System.Security.Claims;

namespace Foundgine.Runtime;

/// <summary>
/// Application-owned intent boundary consumed by transport adapters.
/// The authenticated principal is supplied by the host; it must never be reconstructed
/// from intent arguments or accepted as part of the request payload.
/// </summary>
public interface IFoundgineIntentHandler
{
    Task<FoundgineIntentResult> HandleAsync(
        FoundgineIntentRequest request,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a semantic mutation through the same application-owned boundary as read intents.
    /// Implementations that do not expose mutations fail closed by default.
    /// </summary>
    Task<FoundgineIntentResult> HandleMutationAsync(
        FoundgineMutationIntentRequest request,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(caller?.Identity?.IsAuthenticated != true
            ? FoundgineIntentResult.Failure(
                FoundgineIntentOutcome.Unauthenticated, "unauthenticated", "An authenticated caller is required.")
            : FoundgineIntentResult.Failure(
                FoundgineIntentOutcome.Forbidden, "mutation_not_supported",
                "This application intent boundary does not expose mutations."));
    }
}
