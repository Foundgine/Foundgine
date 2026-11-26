namespace Foundgine.Runtime.Recovery;

/// <summary>Lifecycle of one idempotent mutation execution. Port of the Java <c>MutationExecutionState</c>.</summary>
public enum MutationExecutionState
{
    /// <summary>Intent durably recorded; provider dispatch is pending or in flight. Protected by a lease.</summary>
    Started,

    /// <summary>The provider reported (or reconciliation proved) that the mutation is durable. Terminal.</summary>
    Committed,

    /// <summary>Definitely not applied. Terminal for this attempt; the same key may be retried.</summary>
    Aborted,

    /// <summary>Dispatched, but whether it became durable is unknown. Never retried blindly; must be reconciled.</summary>
    InDoubt,
}

public static class MutationExecutionStateExtensions
{
    public static bool IsTerminalOutcome(this MutationExecutionState state)
        => state is MutationExecutionState.Committed or MutationExecutionState.Aborted;

    /// <summary>The wire name used by the journal: <c>STARTED</c>, <c>COMMITTED</c>, <c>ABORTED</c>, <c>IN_DOUBT</c>.</summary>
    public static string ToWireName(this MutationExecutionState state) => state switch
    {
        MutationExecutionState.Started => "STARTED",
        MutationExecutionState.Committed => "COMMITTED",
        MutationExecutionState.Aborted => "ABORTED",
        MutationExecutionState.InDoubt => "IN_DOUBT",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static MutationExecutionState FromWireName(string value) => value switch
    {
        "STARTED" => MutationExecutionState.Started,
        "COMMITTED" => MutationExecutionState.Committed,
        "ABORTED" => MutationExecutionState.Aborted,
        "IN_DOUBT" => MutationExecutionState.InDoubt,
        _ => throw new ArgumentException($"Unknown mutation execution state '{value}'."),
    };
}
