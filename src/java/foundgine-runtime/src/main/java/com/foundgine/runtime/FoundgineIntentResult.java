package com.foundgine.runtime;

/**
 * Application result returned to a transport adapter without HTTP, GraphQL or MCP response types.
 * Error messages should be safe to expose to the caller; internal exception details belong in logs.
 */
public record FoundgineIntentResult(
        Object data, FoundgineIntentOutcome outcome, String errorCode, String errorMessage) {

    public FoundgineIntentResult {
        if (outcome == null) {
            throw new IllegalArgumentException("outcome is required");
        }
        if (outcome == FoundgineIntentOutcome.SUCCEEDED
                && (errorCode != null || errorMessage != null)) {
            throw new IllegalArgumentException("successful results cannot contain error details");
        }
        if (outcome != FoundgineIntentOutcome.SUCCEEDED && data != null) {
            throw new IllegalArgumentException("failed results cannot contain data");
        }
    }

    public static FoundgineIntentResult success(Object data) {
        return new FoundgineIntentResult(data, FoundgineIntentOutcome.SUCCEEDED, null, null);
    }

    public static FoundgineIntentResult failure(
            FoundgineIntentOutcome outcome, String code, String message) {
        if (outcome == null || outcome == FoundgineIntentOutcome.SUCCEEDED) {
            throw new IllegalArgumentException("failure outcome must not be SUCCEEDED");
        }
        if (code == null || code.isBlank()) {
            throw new IllegalArgumentException("error code is required");
        }
        if (message == null || message.isBlank()) {
            throw new IllegalArgumentException("safe error message is required");
        }
        return new FoundgineIntentResult(null, outcome, code, message);
    }

    public boolean succeeded() {
        return outcome == FoundgineIntentOutcome.SUCCEEDED;
    }
}
