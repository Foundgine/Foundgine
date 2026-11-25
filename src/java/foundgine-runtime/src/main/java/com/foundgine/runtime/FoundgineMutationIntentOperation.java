package com.foundgine.runtime;

/** Lifecycle operation for a semantic mutation submitted through the unified intent boundary. */
public enum FoundgineMutationIntentOperation {
    /** Validate, authorize and describe the proposed plan without executing provider work. */
    DRY_RUN,
    /** Execute a newly submitted semantic mutation. */
    EXECUTE,
    /** Create a server-side approval for the currently authorized plan. */
    APPROVE,
    /** Execute only the exact plan represented by a previously issued approval. */
    EXECUTE_APPROVED
}
