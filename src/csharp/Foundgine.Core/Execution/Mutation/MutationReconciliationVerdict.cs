namespace Foundgine.Core.Execution.Mutation;

/// <summary>What a provider says about a dispatched mutation.</summary>
public enum MutationVerdictOutcome
{
    /// <summary>The mutation is durable.</summary>
    Applied,

    /// <summary>The mutation is not durable and, by the fencing contract, can no longer commit.</summary>
    NotApplied,

    /// <summary>The provider cannot tell.</summary>
    Unknown,
}

/// <summary>
/// A provider's answer about whether a dispatched mutation became durable.
/// </summary>
/// <remarks>
/// <b>Fencing contract.</b> <see cref="MutationVerdictOutcome.NotApplied"/> must be binding: once returned, the
/// provider must guarantee that a late or still-running execution carrying the same request id can no longer commit
/// (for example by inserting a tombstone row under the same unique key the executing transaction inserts its marker
/// into). Without that guarantee a provider must answer <see cref="MutationVerdictOutcome.Unknown"/>, because the
/// runtime will allow a retry after "not applied".
/// </remarks>
public sealed record MutationReconciliationVerdict
{
    public MutationReconciliationVerdict(MutationVerdictOutcome outcome, string? evidenceFingerprint, string? detail)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (outcome == MutationVerdictOutcome.Applied && string.IsNullOrWhiteSpace(evidenceFingerprint))
            throw new ArgumentException("An Applied verdict requires a non-blank evidence fingerprint.", nameof(evidenceFingerprint));
        Outcome = outcome;
        EvidenceFingerprint = evidenceFingerprint;
        Detail = detail;
    }

    public MutationVerdictOutcome Outcome { get; }

    public string? EvidenceFingerprint { get; }

    public string? Detail { get; }

    /// <summary>The mutation is durable. <paramref name="evidenceFingerprint"/> fingerprints the provider's proof (not the data).</summary>
    public static MutationReconciliationVerdict Applied(string evidenceFingerprint, string? detail = null)
        => new(MutationVerdictOutcome.Applied, evidenceFingerprint, detail);

    /// <summary>The mutation is not durable and can no longer commit (see the fencing contract).</summary>
    public static MutationReconciliationVerdict NotApplied(string? detail = null)
        => new(MutationVerdictOutcome.NotApplied, null, detail);

    public static MutationReconciliationVerdict Unknown(string? detail = null)
        => new(MutationVerdictOutcome.Unknown, null, detail);
}
