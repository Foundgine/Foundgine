namespace Foundgine.Core.Execution.Mutation;

/// <summary>
/// Thrown by a mutation provider to assert that the failed batch committed nothing (for example a transaction that
/// was rolled back, or a statement rejected before any write). It is the only provider failure the runtime treats as
/// "definitely not applied", which lets a retry under the same idempotency key execute again.
/// </summary>
/// <remarks>
/// Any other exception leaves the outcome <em>in doubt</em>: a connection can fail after the database committed but
/// before the acknowledgement arrived. Providers must not throw this type unless they can guarantee the batch is not
/// and will not become durable.
/// </remarks>
public class MutationNotAppliedException : Exception
{
    public MutationNotAppliedException(string message) : base(message) { }

    public MutationNotAppliedException(string message, Exception innerException) : base(message, innerException) { }
}
