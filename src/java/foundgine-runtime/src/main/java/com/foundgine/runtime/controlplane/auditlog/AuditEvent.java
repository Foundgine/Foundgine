package com.foundgine.runtime.controlplane.auditlog;

import java.time.Instant;

/** Immutable governance/execution audit event. Payloads are represented by fingerprints only. */
public record AuditEvent(
        AuditCategory category, String toolName, String actor, String tenant,
        String fingerprint, String summary, Instant occurredAt, String correlationId,
        long sequence, String previousHash, String eventHash) {

    public enum AuditCategory {
        RISK_SCORED, POLICY_EVALUATED, APPROVAL_REQUESTED, APPROVAL_DECIDED,
        ROUTED, DENIED, READ_SUCCEEDED, READ_FAILED, READ_DENIED, MUTATION_DRY_RUN, MUTATION_SUCCEEDED, MUTATION_FAILED, MUTATION_DENIED
    }

    /** Source-compatible constructor for events created before correlation and chain metadata. */
    public AuditEvent(AuditCategory category, String toolName, String actor, String tenant,
            String fingerprint, String summary, Instant occurredAt) {
        this(category, toolName, actor, tenant, fingerprint, summary, occurredAt, null, 0, null, null);
    }

    public static AuditEvent create(AuditCategory category, String toolName, String actor,
            String tenant, String fingerprint, String summary) {
        return new AuditEvent(category, toolName, actor, tenant, fingerprint, summary,
                Instant.now(), null, 0, null, null);
    }

    public static AuditEvent createCorrelated(AuditCategory category, String toolName, String actor,
            String tenant, String fingerprint, String summary, String correlationId) {
        return new AuditEvent(category, toolName, actor, tenant, fingerprint, summary,
                Instant.now(), correlationId, 0, null, null);
    }

    public AuditEvent withIntegrity(long nextSequence, String previous, String hash) {
        return new AuditEvent(category, toolName, actor, tenant, fingerprint, summary, occurredAt,
                correlationId, nextSequence, previous, hash);
    }
}
