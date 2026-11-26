package com.foundgine.runtime.controlplane;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.runtime.controlplane.auditlog.*;
import java.time.Instant;
import java.util.List;
import org.junit.jupiter.api.Test;

class HashChainedAuditLogTest {
    @Test void appendsContiguousVerifiableChain() {
        var memory = new InMemoryAuditLog();
        var chain = new HashChainedAuditLog(memory);
        chain.record(event("one"));
        chain.record(event("two"));
        var events = chain.query();
        assertEquals(1, events.get(0).sequence());
        assertEquals(2, events.get(1).sequence());
        assertEquals(events.get(0).eventHash(), events.get(1).previousHash());
        assertTrue(HashChainedAuditLog.verify(events));
    }

    @Test void detectsTamperedEventAndReordering() {
        var chain = new HashChainedAuditLog(new InMemoryAuditLog());
        chain.record(event("one"));
        chain.record(event("two"));
        var events = chain.query();
        var first = events.get(0);
        var tampered = new AuditEvent(first.category(), first.toolName(), first.actor(), first.tenant(),
                first.fingerprint(), "altered", first.occurredAt(), first.correlationId(),
                first.sequence(), first.previousHash(), first.eventHash());
        assertFalse(HashChainedAuditLog.verify(List.of(tampered, events.get(1))));
        assertFalse(HashChainedAuditLog.verify(List.of(events.get(1), events.get(0))));
    }



    @Test void restoresExistingChainBeforeAppending() {
        var memory = new InMemoryAuditLog();
        var firstProcess = new HashChainedAuditLog(memory);
        firstProcess.record(event("one"));
        firstProcess.record(event("two"));
        var restarted = new HashChainedAuditLog(memory);
        restarted.record(event("three"));
        var events = memory.query();
        assertEquals(List.of(1L, 2L, 3L), events.stream().map(AuditEvent::sequence).toList());
        assertTrue(HashChainedAuditLog.verify(events));
    }

    @Test void signsAndVerifiesExternalCheckpoint() {
        var chain = new HashChainedAuditLog(new InMemoryAuditLog());
        chain.record(event("one"));
        var signer = new TestSigner();
        var checkpoint = chain.createCheckpoint(signer);
        assertEquals(1, checkpoint.sequence());
        assertEquals(chain.headHash(), checkpoint.headHash());
        assertTrue(HashChainedAuditLog.verifyCheckpoint(checkpoint, signer));
        var changed = new AuditCheckpoint(checkpoint.sequence() + 1, checkpoint.headHash(),
                checkpoint.keyId(), checkpoint.signatureBase64());
        assertFalse(HashChainedAuditLog.verifyCheckpoint(changed, signer));
    }

    private static final class TestSigner implements IAuditCheckpointSigner {
        private static final byte[] KEY = "test-only-key".getBytes(java.nio.charset.StandardCharsets.UTF_8);
        @Override public String keyId() { return "test-key-1"; }
        @Override public byte[] sign(byte[] payload) { return mac(payload); }
        @Override public boolean verify(byte[] payload, byte[] signature) {
            return java.security.MessageDigest.isEqual(mac(payload), signature);
        }
        private static byte[] mac(byte[] payload) {
            try {
                var mac = javax.crypto.Mac.getInstance("HmacSHA256");
                mac.init(new javax.crypto.spec.SecretKeySpec(KEY, "HmacSHA256"));
                return mac.doFinal(payload);
            } catch (java.security.GeneralSecurityException error) {
                throw new IllegalStateException(error);
            }
        }
    }

    private static AuditEvent event(String name) {
        return new AuditEvent(AuditEvent.AuditCategory.ROUTED, name, "actor", "tenant",
                "fingerprint", "summary", Instant.now());
    }
}
