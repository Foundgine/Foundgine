package com.foundgine.core.execution;

/**
 * Port of {@code Foundgine.Core.Execution.ExecutionContextKeys}: reserved runtime context keys used
 * by provider execution for dynamic request values.
 */
public final class ExecutionContextKeys {

    public static final String PAGINATION_LIMIT = "foundgine.pagination.limit";
    public static final String PAGINATION_OFFSET = "foundgine.pagination.offset";
    public static final String PAGINATION_HAS_CURSOR = "foundgine.pagination.hasCursor";
    /** Host-supplied opaque request correlation identifier; never an authority or tenant claim. */
    public static final String CORRELATION_ID = "foundgine.execution.correlationId";
    /**
     * Caller-supplied, opaque key that makes one logical mutation executable at most once. It is scoped by the
     * engine to the authenticated (tenant, subject), is never an authority claim, and must match
     * {@code [A-Za-z0-9._:-]{1,128}}.
     */
    public static final String IDEMPOTENCY_KEY = "foundgine.execution.idempotencyKey";
    /**
     * Engine-assigned identifier of one mutation execution (the receipt's {@code requestId}). The engine adds it to the
     * context given to the mutation provider so the provider can persist an atomic execution marker with the mutation
     * and later answer {@code IMutationReconciler} queries. Values supplied by callers under this key are ignored.
     */
    public static final String EXECUTION_REQUEST_ID = "foundgine.execution.requestId";

    private ExecutionContextKeys() {}
}
