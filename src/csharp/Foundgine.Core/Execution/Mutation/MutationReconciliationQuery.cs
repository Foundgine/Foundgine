namespace Foundgine.Core.Execution.Mutation;

/// <summary>
/// What the runtime knows about an execution whose outcome is unknown. Carries identifiers and fingerprints only,
/// never request or result payloads.
/// </summary>
/// <param name="RequestId">The engine-assigned execution id, also given to the provider when it ran.</param>
/// <param name="Tenant">Tenant of the idempotency scope (may be null).</param>
/// <param name="Subject">Subject of the idempotency scope (may be null).</param>
/// <param name="IdempotencyKey">The idempotency key of the execution.</param>
/// <param name="PlanFingerprint">Fingerprint of the authorized plan that was dispatched.</param>
/// <param name="CorrelationId">The correlation id of the original request.</param>
/// <param name="StartedAt">When the engine recorded the intent to execute.</param>
public sealed record MutationReconciliationQuery(
    string RequestId,
    string? Tenant,
    string? Subject,
    string IdempotencyKey,
    string PlanFingerprint,
    string? CorrelationId,
    DateTimeOffset StartedAt);
