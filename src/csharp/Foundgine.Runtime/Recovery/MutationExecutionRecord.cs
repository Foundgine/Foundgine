using System.Text.RegularExpressions;
using Foundgine.Core.Execution;

namespace Foundgine.Runtime.Recovery;

/// <summary>
/// Durable, payload-free record of one mutation execution, keyed by (tenant, subject, idempotency key).
/// Port of the Java <c>MutationExecutionRecord</c>.
/// </summary>
public sealed partial record MutationExecutionRecord
{
    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}\\z")]
    private static partial Regex SafeId();

    public MutationExecutionRecord(
        string? tenant, string? subject, string idempotencyKey, string requestId, string planFingerprint,
        string? correlationId, MutationExecutionState state, int attempt, long version, DateTimeOffset startedAt,
        DateTimeOffset updatedAt, DateTimeOffset? leaseExpiresAt, ExecutionReceipt receipt,
        IReadOnlyList<int>? affectedRows, bool auditAcknowledged, string? resolution)
    {
        if (idempotencyKey is null || !SafeId().IsMatch(idempotencyKey))
            throw new ArgumentException("idempotencyKey must contain 1-128 safe identifier characters", nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentNullException.ThrowIfNull(planFingerprint);
        ArgumentNullException.ThrowIfNull(receipt);
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "attempt must be >= 1");
        Tenant = tenant; Subject = subject; IdempotencyKey = idempotencyKey; RequestId = requestId;
        PlanFingerprint = planFingerprint; CorrelationId = correlationId; State = state; Attempt = attempt;
        Version = version; StartedAt = startedAt; UpdatedAt = updatedAt; LeaseExpiresAt = leaseExpiresAt;
        Receipt = receipt; AffectedRows = affectedRows is null ? Array.Empty<int>() : affectedRows.ToArray();
        AuditAcknowledged = auditAcknowledged; Resolution = resolution;
    }

    /// <summary>True for 1-128 characters from <c>A-Za-z0-9._:-</c>; the rule for idempotency keys and operator ids.</summary>
    public static bool IsSafeId(string? value) => value is not null && SafeId().IsMatch(value);

    public string? Tenant { get; }
    public string? Subject { get; }
    public string IdempotencyKey { get; }
    public string RequestId { get; }
    public string PlanFingerprint { get; }
    public string? CorrelationId { get; }
    public MutationExecutionState State { get; }
    public int Attempt { get; }
    public long Version { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public DateTimeOffset? LeaseExpiresAt { get; }
    public ExecutionReceipt Receipt { get; }
    public IReadOnlyList<int> AffectedRows { get; }
    public bool AuditAcknowledged { get; }
    public string? Resolution { get; }

    /// <summary>Stable identity of the idempotency scope + key (unambiguous for null/empty tenant or subject).</summary>
    public string Identity => MakeIdentity(Tenant, Subject, IdempotencyKey);

    public static string MakeIdentity(string? tenant, string? subject, string idempotencyKey)
        => Enc(tenant) + "\u001f" + Enc(subject) + "\u001f" + idempotencyKey;

    private static string Enc(string? v) => v is null ? "\u0000" : "s" + v;

    public MutationExecutionRecord WithVersion(long value) => new(
        Tenant, Subject, IdempotencyKey, RequestId, PlanFingerprint, CorrelationId, State, Attempt, value, StartedAt,
        UpdatedAt, LeaseExpiresAt, Receipt, AffectedRows, AuditAcknowledged, Resolution);

    public MutationExecutionRecord Transition(MutationExecutionState next, DateTimeOffset now,
        ExecutionReceipt nextReceipt, IReadOnlyList<int> rows, string? nextResolution) => new(
        Tenant, Subject, IdempotencyKey, RequestId, PlanFingerprint, CorrelationId, next, Attempt, Version, StartedAt,
        now, null, nextReceipt, rows, AuditAcknowledged, nextResolution);

    public MutationExecutionRecord WithAuditAcknowledged(DateTimeOffset now) => new(
        Tenant, Subject, IdempotencyKey, RequestId, PlanFingerprint, CorrelationId, State, Attempt, Version, StartedAt,
        now, LeaseExpiresAt, Receipt, AffectedRows, true, Resolution);

    /// <summary>A new attempt after an <c>Aborted</c> one: new request id, fresh provisional receipt and lease.</summary>
    public MutationExecutionRecord Reopen(string newRequestId, string? newCorrelationId, DateTimeOffset now,
        DateTimeOffset lease, ExecutionReceipt provisional) => new(
        Tenant, Subject, IdempotencyKey, newRequestId, PlanFingerprint, newCorrelationId, MutationExecutionState.Started,
        Attempt + 1, Version, now, now, lease, provisional, Array.Empty<int>(), false, null);
}
