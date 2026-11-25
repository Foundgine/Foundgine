using System.Text;
using Foundgine.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundgine.Providers.Tools.Http;

/// <summary>
/// Maps the reusable HTTP surface for an application's <see cref="IFoundgineIntentHandler"/>.
/// The application owns semantic resolution and authorization; this adapter only extracts
/// the authenticated principal and translates HTTP requests into protocol-neutral commands.
/// </summary>
public static class FoundgineIntentEndpoints
{
    public const int DefaultMaxBodyBytes = 65_536;

    /// <summary>
    /// Maps POST <c>/intent</c>, POST <c>/intent/explain</c>, GET <c>/intent/capabilities</c> and POST
    /// <c>/intent/mutation</c>. The mutation route answers 404 <c>mutation_not_configured</c> unless the
    /// application registered an <see cref="IFoundgineMutationDispatcher"/>.
    /// A wrong verb returns 405 and any other path under the route returns 404, both with a JSON error body.
    /// The handler is resolved from request services, so it may be scoped to the host request.
    /// </summary>
    public static IEndpointRouteBuilder MapFoundgineIntentEndpoints(
        this IEndpointRouteBuilder endpoints,
        string path = "/intent",
        int maxBodyBytes = DefaultMaxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new ArgumentException("The intent route must be an absolute application path.", nameof(path));
        if (maxBodyBytes is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(maxBodyBytes), "The body limit must be between 1 byte and 1 MiB.");

        // Each route accepts every verb and checks the verb itself, so that a wrong method gets the same
        // structured error body as every other failure. A verb-constrained route would instead fall through
        // to the catch-all below and be reported as an unknown path.
        endpoints.Map(path, Route(HttpMethods.Post, FoundgineIntentOperation.Execute, maxBodyBytes));
        endpoints.Map(path + "/explain", Route(HttpMethods.Post, FoundgineIntentOperation.Explain, maxBodyBytes));
        endpoints.Map(path + "/capabilities", Route(HttpMethods.Get, FoundgineIntentOperation.Capabilities, maxBodyBytes));
        endpoints.Map(path + "/mutation", MutationRoute(maxBodyBytes));
        endpoints.Map(path + "/{**rest}", (RequestDelegate)(http =>
            Error(StatusCodes.Status404NotFound, "not_found", "Unknown intent path.").ExecuteAsync(http)));
        return endpoints;
    }

    private static RequestDelegate Route(string method, FoundgineIntentOperation operation, int maxBodyBytes) =>
        async http =>
        {
            IResult result;
            if (!HttpMethods.Equals(http.Request.Method, method))
            {
                http.Response.Headers.Allow = method;
                result = Error(StatusCodes.Status405MethodNotAllowed, "method_not_allowed", $"Use {method} for this path.");
            }
            else
            {
                result = await HandleAsync(http, operation, maxBodyBytes, http.RequestAborted).ConfigureAwait(false);
            }

            await result.ExecuteAsync(http).ConfigureAwait(false);
        };

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        FoundgineIntentOperation operation,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        if (http.User.Identity?.IsAuthenticated != true)
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "An authenticated caller is required.");

        try
        {
            var intentJson = operation == FoundgineIntentOperation.Capabilities
                ? null
                : await ReadBodyAsync(http.Request, maxBodyBytes, cancellationToken).ConfigureAwait(false);

            var handler = http.RequestServices.GetRequiredService<IFoundgineIntentHandler>();
            var result = await handler
                .HandleAsync(
                    new FoundgineIntentRequest(operation, intentJson, CorrelationIdFor(http.Request)),
                    http.User,
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded
                ? Results.Json(result.Data, statusCode: StatusCodes.Status200OK)
                : Error(StatusFor(result.Outcome), result.ErrorCode ?? "intent_failed",
                    result.ErrorMessage ?? "The intent could not be completed.");
        }
        catch (PayloadTooLargeException exception)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "payload_too_large", exception.Message);
        }
        catch (InvalidCorrelationIdException exception)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_intent", exception.Message);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or BadHttpRequestException))
        {
            // The handler is expected to classify its own failures. Anything that escapes it is logged
            // here and never described to the client.
            http.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(FoundgineIntentEndpoints))
                .LogError(exception, "The Foundgine intent handler failed.");
            return Error(StatusCodes.Status500InternalServerError, "internal_error", "The intent could not be completed.");
        }
    }

    private static RequestDelegate MutationRoute(int maxBodyBytes) =>
        async http =>
        {
            IResult result;
            if (!HttpMethods.IsPost(http.Request.Method))
            {
                http.Response.Headers.Allow = HttpMethods.Post;
                result = Error(StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "Use POST for this path.");
            }
            else
            {
                result = await HandleMutationAsync(http, maxBodyBytes, http.RequestAborted).ConfigureAwait(false);
            }

            await result.ExecuteAsync(http).ConfigureAwait(false);
        };

    private static async Task<IResult> HandleMutationAsync(
        HttpContext http,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        var dispatcher = http.RequestServices.GetService<IFoundgineMutationDispatcher>();
        if (dispatcher is null)
            return Error(StatusCodes.Status404NotFound, "mutation_not_configured",
                "Mutations are not configured on this endpoint.");
        if (http.User.Identity?.IsAuthenticated != true)
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "An authenticated caller is required.");

        try
        {
            var body = await ReadBodyAsync(http.Request, maxBodyBytes, cancellationToken).ConfigureAwait(false);
            var result = await dispatcher
                .DispatchAsync(body, http.User, CorrelationIdFor(http.Request), cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded
                ? Results.Json(result.Data, statusCode: StatusCodes.Status200OK)
                : Error(StatusFor(result.Outcome), result.ErrorCode ?? "mutation_failed",
                    result.ErrorMessage ?? "The mutation could not be completed.");
        }
        catch (PayloadTooLargeException exception)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "payload_too_large", exception.Message);
        }
        catch (InvalidCorrelationIdException exception)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_intent", exception.Message);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or BadHttpRequestException))
        {
            http.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(FoundgineIntentEndpoints))
                .LogError(exception, "The Foundgine mutation dispatcher failed.");
            return Error(StatusCodes.Status500InternalServerError, "internal_error", "The mutation could not be completed.");
        }
    }

    /// <summary>The caller's trace id, or a generated one. An unsafe supplied value is rejected, never echoed.</summary>
    private static string CorrelationIdFor(HttpRequest request)
    {
        var supplied = request.Headers[FoundgineCorrelation.Header].ToString();
        if (string.IsNullOrWhiteSpace(supplied)) return Guid.NewGuid().ToString();
        if (!FoundgineCorrelation.IsValid(supplied))
            throw new InvalidCorrelationIdException(
                $"{FoundgineCorrelation.Header} must be 1-128 safe identifier characters.");
        return supplied;
    }

    private static int StatusFor(FoundgineIntentOutcome outcome) => outcome switch
    {
        FoundgineIntentOutcome.Unauthenticated => StatusCodes.Status401Unauthorized,
        FoundgineIntentOutcome.Forbidden => StatusCodes.Status403Forbidden,
        FoundgineIntentOutcome.InvalidIntent => StatusCodes.Status400BadRequest,
        FoundgineIntentOutcome.Timeout => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status500InternalServerError
    };

    private static IResult Error(int statusCode, string type, string message) =>
        Results.Json(new { error = new { type, message } }, statusCode: statusCode);

    private static async Task<string> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
            throw new PayloadTooLargeException($"An intent may not exceed {maxBytes} bytes.");

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new PayloadTooLargeException($"An intent may not exceed {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private sealed class PayloadTooLargeException(string message) : Exception(message);

    private sealed class InvalidCorrelationIdException(string message) : Exception(message);
}
