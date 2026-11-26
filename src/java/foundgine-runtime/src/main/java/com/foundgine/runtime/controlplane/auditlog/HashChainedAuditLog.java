package com.foundgine.runtime.controlplane.auditlog;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HexFormat;
import java.util.Base64;
import java.util.List;
import java.util.Objects;

/**
 * Serializes audit appends into a SHA-256 hash chain before forwarding them to the configured log.
 * The wrapped log may be {@link BufferedDurableAuditLog} for acknowledged durable persistence.
 * Protect the chain head and external store independently; an unkeyed hash chain is tamper-evident
 * against partial edits/reordering, not an authenticated signature against a privileged attacker.
 */
public final class HashChainedAuditLog implements IAuditLog {
    private static final String GENESIS = "0".repeat(64);
    private final IAuditLog inner;
    private long sequence;
    private String head = GENESIS;

    public HashChainedAuditLog(IAuditLog inner) {
        this(inner, Objects.requireNonNull(inner, "inner").query());
    }

    /** Restores the chain head from a complete, ordered history loaded by the host. */
    public HashChainedAuditLog(IAuditLog inner, List<AuditEvent> existingHistory) {
        this.inner = Objects.requireNonNull(inner, "inner");
        Objects.requireNonNull(existingHistory, "existingHistory");
        List<AuditEvent> visibleHistory = this.inner.query();
        if (!visibleHistory.isEmpty() && !visibleHistory.equals(existingHistory))
            throw new IllegalArgumentException("Restoration history does not match the wrapped audit log");
        if (!existingHistory.isEmpty()) {
            if (!verify(existingHistory))
                throw new IllegalArgumentException("Existing audit history is not a valid contiguous hash chain");
            AuditEvent last = existingHistory.get(existingHistory.size() - 1);
            this.sequence = last.sequence();
            this.head = last.eventHash();
        }
    }

    @Override public synchronized void record(AuditEvent event) {
        Objects.requireNonNull(event, "event");
        long next = sequence + 1;
        String hash = hash(canonical(event, next, head));
        AuditEvent chained = event.withIntegrity(next, head, hash);
        // Persist first: do not advance the in-memory head if the durable append fails.
        inner.record(chained);
        sequence = next;
        head = hash;
    }

    @Override public synchronized List<AuditEvent> query(String toolName, String tenant) {
        return inner.query(toolName, tenant);
    }

    public synchronized String headHash() { return head; }

    public synchronized long sequence() { return sequence; }

    /** Signs the current chain head using a host-owned protected signing key. */
    public synchronized AuditCheckpoint createCheckpoint(IAuditCheckpointSigner signer) {
        Objects.requireNonNull(signer, "signer");
        String keyId = Objects.requireNonNull(signer.keyId(), "signer.keyId");
        if (keyId.isBlank()) throw new IllegalArgumentException("signer.keyId must not be blank");
        byte[] payload = checkpointPayload(sequence, head);
        byte[] signature = Objects.requireNonNull(signer.sign(payload), "checkpoint signature");
        if (signature.length == 0) throw new IllegalStateException("checkpoint signer returned an empty signature");
        return new AuditCheckpoint(sequence, head, keyId, Base64.getEncoder().encodeToString(signature));
    }

    /** Verifies a checkpoint against the supplied host key resolver/signer. */
    public static boolean verifyCheckpoint(AuditCheckpoint checkpoint, IAuditCheckpointSigner signer) {
        Objects.requireNonNull(checkpoint, "checkpoint");
        Objects.requireNonNull(signer, "signer");
        if (!Objects.equals(checkpoint.keyId(), signer.keyId())) return false;
        try {
            byte[] signature = Base64.getDecoder().decode(checkpoint.signatureBase64());
            return signer.verify(checkpointPayload(checkpoint.sequence(), checkpoint.headHash()), signature);
        } catch (IllegalArgumentException malformed) {
            return false;
        }
    }

    private static byte[] checkpointPayload(long sequence, String headHash) {
        return ("foundgine-audit-checkpoint-v1\n" + sequence + "\n" + headHash)
                .getBytes(StandardCharsets.UTF_8);
    }

    /** Verifies sequence continuity, predecessor links, and each event digest in the returned log. */
    public static boolean verify(List<AuditEvent> events) {
        String previous = GENESIS;
        long expected = 1;
        for (AuditEvent event : events) {
            if (event.sequence() != expected || !Objects.equals(event.previousHash(), previous)) return false;
            if (!Objects.equals(event.eventHash(), hash(canonical(event, expected, previous)))) return false;
            previous = event.eventHash();
            expected++;
        }
        return true;
    }

    private static String canonical(AuditEvent e, long seq, String prev) {
        return seq + "\n" + prev + "\n" + e.category() + "\n" + safe(e.toolName()) + "\n"
                + safe(e.actor()) + "\n" + safe(e.tenant()) + "\n" + safe(e.fingerprint()) + "\n"
                + safe(e.summary()) + "\n" + e.occurredAt() + "\n" + safe(e.correlationId());
    }
    private static String safe(String value) { return value == null ? "" : value; }
    private static String hash(String value) {
        try { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256")
                .digest(value.getBytes(StandardCharsets.UTF_8))); }
        catch (NoSuchAlgorithmException ex) { throw new IllegalStateException("SHA-256 unavailable", ex); }
    }
}
