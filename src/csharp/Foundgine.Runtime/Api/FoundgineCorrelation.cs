using System.Text.RegularExpressions;

namespace Foundgine.Runtime;

/// <summary>
/// Validation shared by every intent and mutation contract for the caller-supplied correlation identifier.
/// Mirrors the Java runtime: 1-128 characters from <c>[A-Za-z0-9._:-]</c>, or null.
/// </summary>
public static class FoundgineCorrelation
{
    public const string Header = "X-Correlation-ID";

    private static readonly Regex Safe = new(
        @"\A[A-Za-z0-9._:-]{1,128}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValid(string? value) => value is null || Safe.IsMatch(value);

    /// <summary>Returns <paramref name="value"/> unchanged, or throws when it is not a safe identifier.</summary>
    public static string? Validate(string? value) =>
        IsValid(value)
            ? value
            : throw new ArgumentException(
                "correlationId must contain 1-128 safe identifier characters", nameof(value));
}
