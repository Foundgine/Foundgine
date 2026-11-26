namespace Foundgine.Runtime;

/// <summary>Operation requested against an application's intent boundary.</summary>
public enum FoundgineIntentOperation
{
    Execute,
    Explain,
    Capabilities
}

/// <summary>A protocol-neutral intent command. Identity and authority are deliberately absent.</summary>
public sealed record FoundgineIntentRequest(
    FoundgineIntentOperation Operation,
    string? IntentJson = null,
    string? CorrelationId = null)
{
    /// <summary>Caller-supplied trace identifier, validated as 1-128 safe identifier characters.</summary>
    public string? CorrelationId { get; } = FoundgineCorrelation.Validate(CorrelationId);
}

/// <summary>
/// How an intent ended, without reference to any transport. Each protocol adapter translates this into
/// its own status or error shape.
/// </summary>
public enum FoundgineIntentOutcome
{
    Succeeded,

    /// <summary>There is no authenticated caller.</summary>
    Unauthenticated,

    /// <summary>The caller is known but is not allowed to do this.</summary>
    Forbidden,

    /// <summary>The intent is missing, malformed or names something that does not exist.</summary>
    InvalidIntent,

    /// <summary>The intent did not finish within its deadline.</summary>
    Timeout,

    /// <summary>Any other failure.</summary>
    Failed
}

/// <summary>Application result returned to a protocol adapter without transport-specific response types.</summary>
public sealed record FoundgineIntentResult(
    object? Data,
    FoundgineIntentOutcome Outcome = FoundgineIntentOutcome.Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool Succeeded => Outcome == FoundgineIntentOutcome.Succeeded;

    public static FoundgineIntentResult Success(object? data) => new(data);

    /// <param name="outcome">Any outcome other than <see cref="FoundgineIntentOutcome.Succeeded"/>.</param>
    /// <param name="code">Application error code, for detail beyond the outcome.</param>
    /// <param name="message">A message that is safe to show to the caller.</param>
    public static FoundgineIntentResult Failure(FoundgineIntentOutcome outcome, string code, string message) =>
        outcome == FoundgineIntentOutcome.Succeeded
            ? throw new ArgumentException("A failure cannot have a successful outcome.", nameof(outcome))
            : new(null, outcome, code, message);
}
