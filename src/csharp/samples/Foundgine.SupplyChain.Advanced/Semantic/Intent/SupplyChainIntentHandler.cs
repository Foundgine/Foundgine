using System.Security.Claims;
using Foundgine.Runtime;

namespace Foundgine.SupplyChain.Advanced.Intent;

/// <summary>
/// Application adapter between the reusable Foundgine protocol integrations and the
/// Supply Chain semantic service. It is intentionally kept in the sample application:
/// caller mapping, error classification and the Supply Chain intent contract are not
/// provider responsibilities.
/// </summary>
public sealed class SupplyChainIntentHandler(SupplyChainIntentService service) : IFoundgineIntentHandler
{
    public async Task<FoundgineIntentResult> HandleAsync(
        FoundgineIntentRequest request,
        ClaimsPrincipal callerPrincipal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerPrincipal);

        try
        {
            if (callerPrincipal.Identity?.IsAuthenticated != true)
                throw new UnauthorizedAccessException("An authenticated caller is required.");

            var caller = IntentCaller.Resolve(callerPrincipal, service.KnownTenants);
            object result = request.Operation switch
            {
                FoundgineIntentOperation.Execute => await service.ExecuteAsync(
                    caller,
                    RequireIntent(request.IntentJson),
                    cancellationToken).ConfigureAwait(false),
                FoundgineIntentOperation.Explain => service.Explain(caller, RequireIntent(request.IntentJson)),
                FoundgineIntentOperation.Capabilities => service.Capabilities(caller),
                _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported intent operation.")
            };

            return FoundgineIntentResult.Success(result);
        }
        catch (UnauthorizedAccessException exception)
        {
            return FoundgineIntentResult.Failure(FoundgineIntentOutcome.Unauthenticated, "unauthenticated", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = SupplyChainIntentService.Classify(exception);
            return FoundgineIntentResult.Failure(Outcome(failure), failure.Type, failure.Message);
        }
    }

    // The service classifies with the statuses of the sample's wire format; the shared contract is neutral.
    private static FoundgineIntentOutcome Outcome(SupplyChainIntentService.Failure failure) => failure.Status switch
    {
        400 => FoundgineIntentOutcome.InvalidIntent,
        403 => FoundgineIntentOutcome.Forbidden,
        504 => FoundgineIntentOutcome.Timeout,
        _ => FoundgineIntentOutcome.Failed
    };

    private static string RequireIntent(string? intentJson) =>
        string.IsNullOrWhiteSpace(intentJson)
            ? throw new ArgumentException("An intent JSON document is required.", nameof(intentJson))
            : intentJson;
}
