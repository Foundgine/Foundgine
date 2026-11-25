namespace Foundgine.Runtime.Recovery;

/// <summary>
/// The mutation was dispatched but whether it became durable is unknown (a provider failure other than
/// <see cref="Foundgine.Core.Execution.Mutation.MutationNotAppliedException"/>, or an earlier attempt whose lease
/// expired). The same idempotency key must not be executed again until it has been reconciled; see
/// <see cref="MutationRecovery"/>.
/// </summary>
public sealed class MutationInDoubtException : Exception
{
    public MutationInDoubtException(string requestId, string idempotencyKey, Exception? innerException = null)
        : base($"The mutation outcome is unknown; reconcile before retrying (requestId={requestId}, idempotencyKey={idempotencyKey}).", innerException)
    {
        RequestId = requestId;
        IdempotencyKey = idempotencyKey;
    }

    public string RequestId { get; }

    public string IdempotencyKey { get; }
}
