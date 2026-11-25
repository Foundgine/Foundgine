package com.foundgine.runtime.recovery;

import com.foundgine.core.execution.ExecutionReceipt;
import com.foundgine.core.execution.mutation.IMutationReconciler;
import com.foundgine.core.execution.mutation.MutationReconciliationQuery;
import com.foundgine.core.execution.mutation.MutationReconciliationVerdict;
import com.foundgine.runtime.controlplane.auditlog.AuditEvent;
import com.foundgine.runtime.controlplane.auditlog.IAuditLog;
import java.time.Clock;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Objects;
import java.util.Optional;

/**
 * Resolves mutation executions whose outcome the engine could not settle: a crash or lost response (STARTED past its
 * lease), a provider failure after dispatch (IN_DOUBT), or a committed mutation whose success audit event was never
 * acknowledged. Resolution is driven by the provider's {@link IMutationReconciler}; an operator can resolve an
 * IN_DOUBT record by hand with {@link #resolve}. Success audit repair is at-least-once: if the original audit write
 * actually persisted but was not acknowledged, a second event is appended.
 */
public final class MutationRecovery {
    private static final int MAX_CAS_ATTEMPTS = 3;

    private final IMutationExecutionLedger ledger;
    private final IMutationReconciler reconciler;
    private final IAuditLog auditLog;
    private final Clock clock;

    public MutationRecovery(IMutationExecutionLedger ledger, IMutationReconciler reconciler, IAuditLog auditLog) {
        this(ledger, reconciler, auditLog, Clock.systemUTC());
    }

    public MutationRecovery(IMutationExecutionLedger ledger, IMutationReconciler reconciler, IAuditLog auditLog,
            Clock clock) {
        this.ledger = Objects.requireNonNull(ledger, "ledger");
        this.reconciler = reconciler;
        this.auditLog = Objects.requireNonNull(auditLog, "auditLog");
        this.clock = Objects.requireNonNull(clock, "clock");
    }

    public MutationReconciliationReport reconcile(String tenant, String subject, String idempotencyKey) {
        boolean changed = false;
        for (int attempt = 0; attempt < MAX_CAS_ATTEMPTS; attempt++) {
            Optional<MutationExecutionRecord> found = ledger.find(tenant, subject, idempotencyKey);
            if (found.isEmpty())
                return new MutationReconciliationReport(MutationReconciliationReport.Outcome.NOT_FOUND, null,
                        changed, "No execution is recorded for this idempotency key.");
            MutationExecutionRecord rec = found.get();
            Instant now = clock.instant();
            switch (rec.state()) {
                case ABORTED:
                    return report(MutationReconciliationReport.Outcome.ABORTED, rec, changed, "Not applied; the key may be retried.");
                case COMMITTED:
                    return repairAudit(rec, changed);
                case STARTED:
                    if (rec.leaseExpiresAt() != null && now.isBefore(rec.leaseExpiresAt()))
                        return report(MutationReconciliationReport.Outcome.IN_PROGRESS, rec, changed,
                                "Execution is still within its lease.");
                    var doubt = ledger.tryUpdate(rec.version(), rec.transition(MutationExecutionState.IN_DOUBT, now,
                            rec.receipt(), rec.affectedRows(), "lease expired without a recorded outcome"));
                    if (doubt.isEmpty()) continue;
                    changed = true;
                    rec = doubt.get();
                    // fall through to the provider check with the IN_DOUBT record
                case IN_DOUBT:
                    MutationReconciliationVerdict verdict = ask(rec);
                    if (verdict == null || verdict.outcome() == MutationReconciliationVerdict.Outcome.UNKNOWN)
                        return report(MutationReconciliationReport.Outcome.UNRESOLVED, rec, changed, reconciler == null
                                ? "No reconciler is configured; resolve the execution manually."
                                : "The provider could not tell whether the mutation was applied.");
                    boolean applied = verdict.outcome() == MutationReconciliationVerdict.Outcome.APPLIED;
                    var next = applied
                            ? rec.transition(MutationExecutionState.COMMITTED, now,
                                    rec.receipt().withOutcome("reconciled", verdict.evidenceFingerprint(), now),
                                    rec.affectedRows(), "reconciled: provider confirmed the mutation")
                            : rec.transition(MutationExecutionState.ABORTED, now,
                                    rec.receipt().withOutcome("aborted", "aborted", now), List.of(),
                                    "reconciled: provider confirmed the mutation was not applied");
                    var stored = ledger.tryUpdate(rec.version(), next);
                    if (stored.isEmpty()) continue; // another writer moved first; re-read
                    if (applied) return repairAudit(stored.get(), true);
                    return report(MutationReconciliationReport.Outcome.ABORTED, stored.get(), true,
                            "Provider confirmed the mutation was not applied; the key may be retried.");
            }
        }
        var latest = ledger.find(tenant, subject, idempotencyKey).orElse(null);
        return report(MutationReconciliationReport.Outcome.UNRESOLVED, latest, changed,
                "The record kept changing; retry reconciliation.");
    }

    /** Reconciles up to {@code limit} unresolved executions, oldest first. */
    public List<MutationReconciliationReport> sweep(int limit) {
        List<MutationReconciliationReport> reports = new ArrayList<>();
        for (var rec : ledger.listUnresolved(limit))
            reports.add(reconcile(rec.tenant(), rec.subject(), rec.idempotencyKey()));
        return reports;
    }

    /**
     * Operator override for an {@code IN_DOUBT} execution after checking the target system by hand. {@code applied}
     * true records COMMITTED, false records ABORTED (which lets the key be retried: only choose it when the mutation
     * is verifiably absent and cannot still commit).
     */
    public MutationReconciliationReport resolve(String tenant, String subject, String idempotencyKey,
            boolean applied, String operatorId, String note) {
        if (operatorId == null || !operatorId.matches(MutationExecutionRecord.SAFE_ID))
            throw new IllegalArgumentException("operatorId must contain 1-128 safe identifier characters");
        var rec = ledger.find(tenant, subject, idempotencyKey).orElse(null);
        if (rec == null)
            return new MutationReconciliationReport(MutationReconciliationReport.Outcome.NOT_FOUND, null, false,
                    "No execution is recorded for this idempotency key.");
        if (rec.state() != MutationExecutionState.IN_DOUBT)
            throw new IllegalStateException("Only an IN_DOUBT execution can be resolved manually (state is " + rec.state() + ").");
        String text = "operator " + operatorId + (note == null || note.isBlank() ? "" : ": " + sanitize(note));
        Instant now = clock.instant();
        var next = applied
                ? rec.transition(MutationExecutionState.COMMITTED, now,
                        rec.receipt().withOutcome("reconciled", sha("manual:" + rec.requestId()), now), rec.affectedRows(), text)
                : rec.transition(MutationExecutionState.ABORTED, now,
                        rec.receipt().withOutcome("aborted", "aborted", now), List.of(), text);
        var stored = ledger.tryUpdate(rec.version(), next);
        if (stored.isEmpty())
            throw new IllegalStateException("The execution changed while it was being resolved; re-read and retry.");
        if (applied) return repairAudit(stored.get(), true);
        // "Not applied" re-opens the key, so the override itself must leave an audit trail. The ledger change is already
        // durable; a failing audit sink is reported, not hidden.
        String detail = "Resolved as not applied by an operator.";
        try {
            auditLog.record(AuditEvent.createCorrelated(AuditEvent.AuditCategory.MUTATION_FAILED, "semantic_mutation",
                    operatorId, rec.tenant(), sha("manual:" + rec.requestId()),
                    "Operator resolved execution as not applied; receipt=" + rec.requestId(), rec.correlationId()));
        } catch (RuntimeException auditFailure) {
            detail += " The audit event for this decision was NOT persisted.";
        }
        return report(MutationReconciliationReport.Outcome.ABORTED, stored.get(), true, detail);
    }

    /** The provider's verdict, or null when there is no reconciler or it failed. */
    private MutationReconciliationVerdict ask(MutationExecutionRecord rec) {
        if (reconciler == null) return null;
        try {
            return reconciler.reconcile(new MutationReconciliationQuery(rec.requestId(), rec.tenant(),
                    rec.subject(), rec.idempotencyKey(), rec.planFingerprint(), rec.correlationId(), rec.startedAt()));
        } catch (RuntimeException failure) {
            return null;
        }
    }

    /** For a COMMITTED record whose success audit was not acknowledged, appends the event and records the ack. */
    private MutationReconciliationReport repairAudit(MutationExecutionRecord rec, boolean changed) {
        if (rec.auditAcknowledged())
            return report(MutationReconciliationReport.Outcome.COMMITTED, rec, changed, "The mutation is committed.");
        try {
            ExecutionReceipt receipt = rec.receipt();
            auditLog.record(AuditEvent.createCorrelated(AuditEvent.AuditCategory.MUTATION_SUCCEEDED, "semantic_mutation",
                    rec.subject(), rec.tenant(), receipt.resultFingerprint(),
                    "Mutation completed; receipt=" + receipt.requestId() + "; recovered", rec.correlationId()));
        } catch (RuntimeException auditFailure) {
            return report(MutationReconciliationReport.Outcome.COMMITTED, rec, changed,
                    "The mutation is committed; its success audit event is still unacknowledged.");
        }
        var acked = ledger.tryUpdate(rec.version(), rec.withAuditAcknowledged(clock.instant()));
        return report(MutationReconciliationReport.Outcome.COMMITTED, acked.orElse(rec), true,
                acked.isPresent() ? "The mutation is committed; its audit event was recovered."
                        : "The mutation is committed; the audit event was appended but not acknowledged.");
    }

    private static MutationReconciliationReport report(MutationReconciliationReport.Outcome o,
            MutationExecutionRecord r, boolean changed, String detail) {
        return new MutationReconciliationReport(o, r, changed, detail);
    }

    private static String sanitize(String note) {
        String t = note.replaceAll("[^A-Za-z0-9 .,:;_()/-]", "?");
        return t.length() > 200 ? t.substring(0, 200) : t;
    }

    private static String sha(String v) {
        try {
            var d = java.security.MessageDigest.getInstance("SHA-256").digest(v.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            return java.util.HexFormat.of().formatHex(d);
        } catch (java.security.NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }
}
