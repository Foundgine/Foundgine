package com.foundgine.runtime.controlplane.auditlog;

/**
 * Host-owned signing boundary for externally anchoring an audit-chain checkpoint.
 * Implementations should use a protected KMS/HSM or equivalent key store; key material is never
 * exposed to Foundgine.
 */
public interface IAuditCheckpointSigner {
    /** Stable key identifier recorded with the checkpoint, not the key material itself. */
    String keyId();

    /** Signs the canonical checkpoint bytes. */
    byte[] sign(byte[] payload);

    /** Verifies a signature using the key identified by {@link #keyId()}. */
    boolean verify(byte[] payload, byte[] signature);
}
