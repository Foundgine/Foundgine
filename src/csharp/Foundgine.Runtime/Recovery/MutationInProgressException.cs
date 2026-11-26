namespace Foundgine.Runtime.Recovery;

/// <summary>Another execution under the same idempotency key is still within its lease. Retry later.</summary>
public sealed class MutationInProgressException : Exception
{
    public MutationInProgressException(string requestId, string idempotencyKey)
        : base($"A mutation with this idempotency key is already in progress (requestId={requestId}).")
    {
        RequestId = requestId;
        IdempotencyKey = idempotencyKey;
    }

    public string RequestId { get; }

    public string IdempotencyKey { get; }
}
