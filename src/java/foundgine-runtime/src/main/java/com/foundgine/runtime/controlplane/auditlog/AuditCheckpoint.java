package com.foundgine.runtime.controlplane.auditlog;

import java.util.Objects;

/** Signed external anchor for one exact audit-chain head. */
public record AuditCheckpoint(long sequence, String headHash, String keyId, String signatureBase64) {
    public AuditCheckpoint {
        if (sequence < 0) throw new IllegalArgumentException("sequence must not be negative");
        Objects.requireNonNull(headHash, "headHash");
        Objects.requireNonNull(keyId, "keyId");
        Objects.requireNonNull(signatureBase64, "signatureBase64");
        if (!headHash.matches("[0-9a-f]{64}")) throw new IllegalArgumentException("headHash must be a SHA-256 hex digest");
        if (keyId.isBlank()) throw new IllegalArgumentException("keyId must not be blank");
        if (signatureBase64.isBlank()) throw new IllegalArgumentException("signatureBase64 must not be blank");
    }
}
