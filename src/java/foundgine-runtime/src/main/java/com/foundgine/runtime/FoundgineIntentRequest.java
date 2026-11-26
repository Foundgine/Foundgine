package com.foundgine.runtime;

import com.foundgine.core.semantic.intent.ReadIntent;

/**
 * Protocol-neutral read-intent command submitted to an application's intent boundary.
 *
 * <p>This contract currently covers dynamic read intent operations. Semantic mutation commands
 * continue to use the dedicated mutation intent and execution contracts; they are not coerced into
 * a {@link com.foundgine.core.semantic.intent.ReadIntent}.
 *
 * <p>Authentication, tenant identity and authority are deliberately not request fields. The host
 * derives those from its authenticated session and supplies the resulting trusted security context
 * separately to {@link IFoundgineIntentHandler}.
 */
public record FoundgineIntentRequest(FoundgineIntentOperation operation, ReadIntent intent, String correlationId) {
    /** Source-compatible constructor for callers that do not supply a correlation identifier. */
    public FoundgineIntentRequest(FoundgineIntentOperation operation, ReadIntent intent) {
        this(operation, intent, null);
    }
    public FoundgineIntentRequest {
        if (operation == null) {
            throw new IllegalArgumentException("operation is required");
        }
        if (operation != FoundgineIntentOperation.CAPABILITIES && intent == null) {
            throw new IllegalArgumentException("intent is required for execute and explain operations");
        }
        if (operation == FoundgineIntentOperation.CAPABILITIES && intent != null) {
            throw new IllegalArgumentException("capabilities operation must not include an intent");
        }
        validateCorrelationId(correlationId);
    }

    private static void validateCorrelationId(String value) {
        if (value != null && (!value.matches("[A-Za-z0-9._:-]{1,128}")))
            throw new IllegalArgumentException("correlationId must contain 1-128 safe identifier characters");
    }

    public static FoundgineIntentRequest execute(ReadIntent intent) {
        return execute(intent, null);
    }

    public static FoundgineIntentRequest execute(ReadIntent intent, String correlationId) {
        return new FoundgineIntentRequest(FoundgineIntentOperation.EXECUTE, intent, correlationId);
    }

    public static FoundgineIntentRequest explain(ReadIntent intent) {
        return new FoundgineIntentRequest(FoundgineIntentOperation.EXPLAIN, intent, null);
    }

    public static FoundgineIntentRequest capabilities() {
        return new FoundgineIntentRequest(FoundgineIntentOperation.CAPABILITIES, null);
    }
}
