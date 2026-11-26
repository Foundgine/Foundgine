package com.foundgine.runtime;

/** Transport-independent outcome of handling an intent request. */
public enum FoundgineIntentOutcome {
    SUCCEEDED,
    /** No authenticated caller was supplied by the host. */
    UNAUTHENTICATED,
    /** The authenticated caller is not authorized for the requested operation. */
    FORBIDDEN,
    /** The intent is missing, malformed or cannot be resolved. */
    INVALID_INTENT,
    /** The intent did not finish within its deadline. */
    TIMEOUT,
    /** An unexpected application failure occurred. */
    FAILED
}
