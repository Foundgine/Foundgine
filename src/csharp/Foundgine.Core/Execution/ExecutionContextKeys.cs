namespace Foundgine.Core.Execution;

/// <summary>Reserved runtime context keys used by provider execution for dynamic request values.</summary>
public static class ExecutionContextKeys
{
    public const string PaginationLimit = "foundgine.pagination.limit";
    public const string PaginationOffset = "foundgine.pagination.offset";
    public const string PaginationHasCursor = "foundgine.pagination.hasCursor";

    /// <summary>Caller-supplied correlation id (1-128 characters from <c>A-Za-z0-9._:-</c>) carried into receipts and audit events.</summary>
    public const string CorrelationId = "foundgine.execution.correlationId";

    /// <summary>
    /// Caller-supplied idempotency key (same character rules). When the engine has an execution ledger, a mutation that
    /// carries a key is recorded write-ahead, replayed when already committed and refused when in doubt. Without a
    /// ledger the key is ignored.
    /// </summary>
    public const string IdempotencyKey = "foundgine.execution.idempotencyKey";

    /// <summary>
    /// Engine-owned request id of a ledgered execution, handed to the provider so it can write a reconciliation marker.
    /// A caller-supplied value is removed before dispatch.
    /// </summary>
    public const string ExecutionRequestId = "foundgine.execution.requestId";
}
