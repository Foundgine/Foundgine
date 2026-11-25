package com.foundgine.providers.tools.mcp;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.abstractions.*;
import com.foundgine.core.execution.ExecutionContext;
import com.foundgine.core.execution.ExecutionContextKeys;
import com.foundgine.core.execution.mutation.*;
import com.foundgine.core.semantic.SemanticModelBuilder;
import com.foundgine.core.semantic.authorization.AllowAllSemanticAuthorizationPolicy;
import com.foundgine.runtime.FoundgineMutationEngine;
import com.foundgine.runtime.MutationAuditPersistenceException;
import com.foundgine.runtime.MutationCommittedUnrecordedException;
import com.foundgine.runtime.MutationExecutionResult;
import com.foundgine.runtime.controlplane.auditlog.AuditEvent;
import com.foundgine.runtime.controlplane.auditlog.IAuditLog;
import com.foundgine.runtime.controlplane.auditlog.InMemoryAuditLog;
import com.foundgine.runtime.recovery.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;

/** Write-ahead execution, replay, in-doubt handling and reconciliation against the real {@link FoundgineMutationEngine}. */
class MutationRecoveryEngineTest {
    private static final EntityId CUSTOMER = EntityId.create("Customer");
    private static final FieldId NAME = FieldId.create("Customer", "Name");

    private static String mutation(String name) {
        return "{\"operations\":[{\"entity\":" + Long.toUnsignedString(CUSTOMER.value()) + ",\"kind\":\"create\",\"fields\":{\""
                + Long.toUnsignedString(NAME.value()) + "\":\"" + name + "\"},\"returnFields\":["
                + Long.toUnsignedString(NAME.value()) + "]}]}";
    }

    enum Mode { OK, NOT_APPLIED, FAIL_AFTER_COMMIT, FAIL_BEFORE_COMMIT, CRASH }

    /** A provider with a durable "marker table" so it can honestly answer reconciliation. */
    static final class Provider implements IMutationBatchExecutionProvider, IMutationSecurityConformanceEvaluator, IMutationReconciler {
        final AtomicInteger calls = new AtomicInteger();
        final Set<String> committedRequestIds = new HashSet<>();
        final Set<String> fenced = new HashSet<>();
        final List<ExecutionContext> contexts = new ArrayList<>();
        volatile Mode mode = Mode.OK;
        volatile Runnable duringExecute;
        volatile CountDownLatch entered, release;

        @Override public synchronized MutationBatchResult executeBatch(ExecutionMutationIR ir, ExecutionContext context) {
            calls.incrementAndGet();
            contexts.add(context);
            String requestId = (String) context.effectiveValues().get(ExecutionContextKeys.EXECUTION_REQUEST_ID);
            if (entered != null) {
                entered.countDown();
                try { release.await(5, TimeUnit.SECONDS); } catch (InterruptedException e) { throw new RuntimeException(e); }
            }
            if (duringExecute != null) duringExecute.run();
            switch (mode) {
                case NOT_APPLIED -> throw new MutationNotAppliedException("rolled back");
                case FAIL_BEFORE_COMMIT -> throw new IllegalStateException("connection reset");
                case CRASH -> throw new AssertionError("process died");
                case FAIL_AFTER_COMMIT -> { committedRequestIds.add(requestId); throw new IllegalStateException("ack lost"); }
                default -> committedRequestIds.add(requestId);
            }
            return new MutationBatchResult(List.of(new MutationResult(1)));
        }

        @Override public MutationSecurityConformanceResult evaluate(ExecutionMutationIR ir) {
            return new MutationSecurityConformanceResult(getClass().getName(), ir.requiredSecurityInvariants(), List.of());
        }

        @Override public synchronized MutationReconciliationVerdict reconcile(MutationReconciliationQuery q) {
            if (committedRequestIds.contains(q.requestId())) return MutationReconciliationVerdict.applied("marker:" + q.requestId());
            fenced.add(q.requestId()); // tombstone: a late commit of this request id would now be refused
            return MutationReconciliationVerdict.notApplied("no marker; fenced");
        }
    }

    /** Ledger decorator that can fail on demand. */
    static final class FlakyLedger implements IMutationExecutionLedger {
        final IMutationExecutionLedger inner = new InMemoryMutationExecutionLedger();
        volatile boolean failCreate, failCommit;
        public CreateResult tryCreate(MutationExecutionRecord r) {
            if (failCreate) throw new MutationLedgerException("ledger down");
            return inner.tryCreate(r);
        }
        public Optional<MutationExecutionRecord> tryUpdate(long v, MutationExecutionRecord u) {
            if (failCommit && u.state() == MutationExecutionState.COMMITTED) throw new MutationLedgerException("ledger down");
            return inner.tryUpdate(v, u);
        }
        public Optional<MutationExecutionRecord> find(String t, String s, String k) { return inner.find(t, s, k); }
        public Optional<MutationExecutionRecord> findByRequestId(String id) { return inner.findByRequestId(id); }
        public List<MutationExecutionRecord> listUnresolved(int limit) { return inner.listUnresolved(limit); }
    }

    static final class FailingAudit implements IAuditLog {
        volatile boolean fail;
        final InMemoryAuditLog inner = new InMemoryAuditLog();
        public void record(AuditEvent e) { if (fail) throw new IllegalStateException("audit down"); inner.record(e); }
        public List<AuditEvent> query(String tool, String tenant) { return inner.query(tool, tenant); }
    }

    private static final class Fixture {
        final Provider provider = new Provider();
        final FlakyLedger ledger = new FlakyLedger();
        final FailingAudit audit = new FailingAudit();
        final FoundgineMutationEngine engine;

        Fixture(Duration lease) {
            var columns = new LinkedHashMap<FieldId, ColumnId>();
            columns.put(FieldId.create("Customer", "Id"), new ColumnId(1));
            columns.put(NAME, new ColumnId(2));
            var entitySchema = new MutationEntitySchema(CUSTOMER, "Customer", new LinkedHashSet<>(columns.values()), columns, new ColumnId(1));
            MutationSchema schema = new MutationSchema() {
                @Override public MutationEntitySchema getEntity(EntityId id) {
                    if (CUSTOMER.equals(id)) return entitySchema;
                    throw new NoSuchElementException();
                }
                @Override public MutationRelationshipSchema getRelationship(RelationshipId id) { throw new NoSuchElementException(); }
            };
            var model = new SemanticModelBuilder()
                    .entity(CUSTOMER, "Customer", e -> e.identity(FieldId.create("Customer", "Id"), "Id").field(NAME, "Name", String.class))
                    .build();
            engine = new FoundgineMutationEngine(schema, new AllowAllSemanticAuthorizationPolicy(), provider, model, null, null,
                    null, null, null, null, audit, ledger, null, lease);
        }

        MutationExecutionResult run(String name, String key) {
            var ctx = key == null ? ExecutionContext.EMPTY
                    : new ExecutionContext(Map.of(ExecutionContextKeys.IDEMPOTENCY_KEY, key));
            return engine.executeAsync(MutationJsonAdapter.parse(mutation(name)), ctx).toCompletableFuture().join();
        }

        MutationExecutionRecord record(String key) { return ledger.find(null, null, key).orElseThrow(); }
        MutationRecovery recovery() { return engine.recovery().orElseThrow(); }
    }

    @Test
    void committedExecutionIsReplayedWithoutDispatchingAgain() {
        var f = new Fixture(null);
        var first = f.run("Ada", "k1");
        var second = f.run("Ada", "k1");
        assertEquals(1, f.provider.calls.get());
        assertEquals(first.receipt().requestId(), second.receipt().requestId());
        assertEquals(MutationExecutionState.COMMITTED, f.record("k1").state());
        assertTrue(f.record("k1").auditAcknowledged());
        var ctx = f.provider.contexts.get(0).effectiveValues();
        assertEquals("k1", ctx.get(ExecutionContextKeys.IDEMPOTENCY_KEY));
        assertEquals(first.receipt().requestId(), ctx.get(ExecutionContextKeys.EXECUTION_REQUEST_ID));
    }

    @Test
    void sameKeyWithDifferentPlanIsRefused() {
        var f = new Fixture(null);
        f.run("Ada", "k1");
        assertThrows(IllegalStateException.class, () -> f.run("Grace", "k1"));
        assertEquals(1, f.provider.calls.get());
    }

    @Test
    void requestsWithoutKeyOrLedgerBehaveAsBefore() {
        var f = new Fixture(null);
        f.run("Ada", null);
        f.run("Ada", null);
        assertEquals(2, f.provider.calls.get());
        assertTrue(f.ledger.listUnresolved(10).isEmpty());
        assertThrows(IllegalArgumentException.class, () -> f.run("Ada", "bad key!"));
        assertEquals(2, f.provider.calls.get());
    }

    @Test
    void explicitNotAppliedAbortsAndAllowsRetryUnderTheSameKey() {
        var f = new Fixture(null);
        f.provider.mode = Mode.NOT_APPLIED;
        assertThrows(MutationNotAppliedException.class, () -> f.run("Ada", "k1"));
        assertEquals(MutationExecutionState.ABORTED, f.record("k1").state());
        f.provider.mode = Mode.OK;
        var retry = f.run("Ada", "k1");
        assertEquals(2, f.provider.calls.get());
        var rec = f.record("k1");
        assertEquals(MutationExecutionState.COMMITTED, rec.state());
        assertEquals(2, rec.attempt());
        assertEquals(retry.receipt().requestId(), rec.requestId());
    }

    @Test
    void lostAcknowledgementLeavesInDoubtAndRetryIsRefusedUntilReconciled() {
        var f = new Fixture(null);
        f.provider.mode = Mode.FAIL_AFTER_COMMIT;
        var ex = assertThrows(MutationInDoubtException.class, () -> f.run("Ada", "k1"));
        assertNotNull(ex.getCause());
        assertEquals(MutationExecutionState.IN_DOUBT, f.record("k1").state());
        f.provider.mode = Mode.OK;
        assertThrows(MutationInDoubtException.class, () -> f.run("Ada", "k1"));
        assertEquals(1, f.provider.calls.get());

        var report = f.recovery().reconcile(null, null, "k1");
        assertEquals(MutationReconciliationReport.Outcome.COMMITTED, report.outcome());
        assertEquals("reconciled", report.record().receipt().status());
        var replay = f.run("Ada", "k1");
        assertEquals(1, f.provider.calls.get(), "reconciled-applied must not re-execute");
        assertEquals(report.record().requestId(), replay.receipt().requestId());
    }

    @Test
    void reconciliationNotAppliedFencesAndAllowsRetry() {
        var f = new Fixture(null);
        f.provider.mode = Mode.FAIL_BEFORE_COMMIT;
        assertThrows(MutationInDoubtException.class, () -> f.run("Ada", "k1"));
        String firstRequestId = f.record("k1").requestId();
        var report = f.recovery().reconcile(null, null, "k1");
        assertEquals(MutationReconciliationReport.Outcome.ABORTED, report.outcome());
        assertTrue(f.provider.fenced.contains(firstRequestId));
        f.provider.mode = Mode.OK;
        f.run("Ada", "k1");
        assertEquals(MutationExecutionState.COMMITTED, f.record("k1").state());
        assertNotEquals(firstRequestId, f.record("k1").requestId());
    }

    @Test
    void crashedExecutionExpiresIntoInDoubtAndIsSweptByRecovery() throws Exception {
        var f = new Fixture(Duration.ofMillis(60));
        f.provider.mode = Mode.CRASH;
        assertThrows(AssertionError.class, () -> f.run("Ada", "k1"));
        assertEquals(MutationExecutionState.STARTED, f.record("k1").state());
        f.provider.mode = Mode.OK;
        assertThrows(MutationInProgressException.class, () -> f.run("Ada", "k1"));
        assertEquals(MutationReconciliationReport.Outcome.IN_PROGRESS, f.recovery().reconcile(null, null, "k1").outcome());
        Thread.sleep(100);
        assertThrows(MutationInDoubtException.class, () -> f.run("Ada", "k1"));
        assertEquals(MutationExecutionState.IN_DOUBT, f.record("k1").state());
        var reports = f.recovery().sweep(10);
        assertEquals(1, reports.size());
        assertEquals(MutationReconciliationReport.Outcome.ABORTED, reports.get(0).outcome()); // nothing was committed
        f.run("Ada", "k1");
        assertEquals(2, f.provider.calls.get());
    }

    @Test
    void auditFailureAfterCommitIsRepairedByRecovery() {
        var f = new Fixture(null);
        f.audit.fail = true;
        var ex = assertThrows(MutationAuditPersistenceException.class, () -> f.run("Ada", "k1"));
        assertNotNull(ex.executionResult().receipt());
        assertEquals(MutationExecutionState.COMMITTED, f.record("k1").state());
        assertFalse(f.record("k1").auditAcknowledged());
        assertEquals(1, f.ledger.listUnresolved(10).size());

        f.audit.fail = false;
        var report = f.recovery().reconcile(null, null, "k1");
        assertEquals(MutationReconciliationReport.Outcome.COMMITTED, report.outcome());
        assertTrue(report.changed());
        assertTrue(f.record("k1").auditAcknowledged());
        assertTrue(f.ledger.listUnresolved(10).isEmpty());
        assertEquals(1, f.provider.calls.get());
    }

    @Test
    void unavailableLedgerRefusesToExecute() {
        var f = new Fixture(null);
        f.ledger.failCreate = true;
        assertThrows(MutationLedgerException.class, () -> f.run("Ada", "k1"));
        assertEquals(0, f.provider.calls.get());
    }

    @Test
    void commitThatCannotBeRecordedSurfacesEvidenceAndRecoversFromTheProvider() {
        var f = new Fixture(null);
        f.ledger.failCommit = true;
        var ex = assertThrows(MutationCommittedUnrecordedException.class, () -> f.run("Ada", "k1"));
        assertEquals(1, ex.executionResult().result().totalAffectedRows());
        assertEquals(MutationExecutionState.STARTED, f.record("k1").state());
        f.ledger.failCommit = false;
        // the lease has not expired, so the record is still protected (the next test covers expiry):
        assertThrows(MutationInProgressException.class, () -> f.run("Ada", "k1"));
    }

    @Test
    void unrecordedCommitIsResolvedAfterTheLeaseExpires() throws Exception {
        var f = new Fixture(Duration.ofMillis(40));
        f.ledger.failCommit = true;
        var ex = assertThrows(MutationCommittedUnrecordedException.class, () -> f.run("Ada", "k1"));
        f.ledger.failCommit = false;
        Thread.sleep(80);
        var report = f.recovery().reconcile(null, null, "k1");
        assertEquals(MutationReconciliationReport.Outcome.COMMITTED, report.outcome());
        assertEquals(ex.executionResult().receipt().requestId(), report.record().requestId());
        f.run("Ada", "k1");
        assertEquals(1, f.provider.calls.get());
    }

    @Test
    void operatorCanResolveAnInDoubtExecutionOnlyWhileItIsInDoubt() {
        var f = new Fixture(null);
        f.provider.mode = Mode.FAIL_BEFORE_COMMIT;
        assertThrows(MutationInDoubtException.class, () -> f.run("Ada", "k1"));
        assertThrows(IllegalArgumentException.class, () -> f.recovery().resolve(null, null, "k1", true, "bad op!", null));
        var r = f.recovery().resolve(null, null, "k1", true, "ops-1", "verified row exists");
        assertEquals(MutationReconciliationReport.Outcome.COMMITTED, r.outcome());
        assertThrows(IllegalStateException.class, () -> f.recovery().resolve(null, null, "k1", false, "ops-1", null));
    }

    @Test
    void concurrentSameKeyExecutesOnce() throws Exception {
        var f = new Fixture(null);
        f.provider.entered = new CountDownLatch(1);
        f.provider.release = new CountDownLatch(1);
        var first = new Thread(() -> f.run("Ada", "k1"));
        first.start();
        assertTrue(f.provider.entered.await(5, TimeUnit.SECONDS));
        assertThrows(MutationInProgressException.class, () -> f.run("Ada", "k1"));
        f.provider.release.countDown();
        first.join();
        assertEquals(1, f.provider.calls.get());
        assertEquals(MutationExecutionState.COMMITTED, f.record("k1").state());
    }

    @Test
    void planFingerprintIsIdenticalInEveryJvm() throws Exception {
        var seen = new HashSet<String>();
        seen.add(FingerprintProbe.fingerprint());
        String launcher = java.nio.file.Path.of(System.getProperty("java.home"), "bin", "java").toString();
        for (int i = 0; i < 6; i++) {
            var process = new ProcessBuilder(launcher, "-cp", childClasspath(), FingerprintProbe.class.getName())
                    .redirectError(ProcessBuilder.Redirect.INHERIT).start();
            String out = new String(process.getInputStream().readAllBytes(), java.nio.charset.StandardCharsets.UTF_8).trim();
            assertEquals(0, process.waitFor());
            seen.add(out);
        }
        assertEquals(1, seen.size(), "fingerprint must survive restarts: " + seen);
    }

    /** Built from loaded code sources: java.class.path is incomplete under some test launchers. */
    private static String childClasspath() throws Exception {
        var entries = new LinkedHashSet<String>();
        for (Class<?> type : List.of(MutationRecoveryEngineTest.class, MutationJsonAdapter.class, FoundgineMutationEngine.class,
                ExecutionContext.class, com.fasterxml.jackson.databind.ObjectMapper.class,
                com.fasterxml.jackson.core.JsonFactory.class, com.fasterxml.jackson.annotation.JsonProperty.class,
                com.fasterxml.jackson.datatype.jsr310.JavaTimeModule.class, org.junit.jupiter.api.Test.class))
            entries.add(java.nio.file.Path.of(type.getProtectionDomain().getCodeSource().getLocation().toURI()).toString());
        return String.join(java.io.File.pathSeparator, entries);
    }

    /** Child-process entry point: prints the dry-run fingerprint of a fixed multi-operation plan. */
    public static final class FingerprintProbe {
        static String fingerprint() {
            var two = "{\"operations\":[" + operation("Ada") + "," + operation("Grace") + "]}";
            return new Fixture(null).engine.dryRun(MutationJsonAdapter.parse(two)).planFingerprint();
        }

        private static String operation(String name) {
            String all = mutation(name);
            return all.substring(all.indexOf('[') + 1, all.lastIndexOf(']'));
        }

        public static void main(String[] args) {
            System.out.println(fingerprint());
        }
    }

    @Test
    void callerSuppliedExecutionRequestIdNeverReachesTheProvider() {
        var f = new Fixture(null);
        for (String key : new String[] {null, "k1"}) {
            var values = new HashMap<String, Object>();
            values.put(ExecutionContextKeys.EXECUTION_REQUEST_ID, "chosen-by-caller");
            if (key != null) values.put(ExecutionContextKeys.IDEMPOTENCY_KEY, key);
            f.engine.executeAsync(MutationJsonAdapter.parse(mutation("Ada")), new ExecutionContext(values)).toCompletableFuture().join();
        }
        assertNull(f.provider.contexts.get(0).effectiveValues().get(ExecutionContextKeys.EXECUTION_REQUEST_ID));
        assertEquals(f.record("k1").requestId(),
                f.provider.contexts.get(1).effectiveValues().get(ExecutionContextKeys.EXECUTION_REQUEST_ID));
    }

    @Test
    void confirmedCommitWinsOverAConcurrentMoveToInDoubt() {
        var f = new Fixture(null);
        f.provider.duringExecute = () -> {
            var current = f.record("k1");
            f.ledger.tryUpdate(current.version(), current.transition(MutationExecutionState.IN_DOUBT,
                    java.time.Instant.now(), current.receipt(), List.of(), "recovery moved it")).orElseThrow();
        };
        var result = f.run("Ada", "k1");
        assertEquals(MutationExecutionState.COMMITTED, f.record("k1").state());
        assertEquals(result.receipt().requestId(), f.record("k1").requestId());
    }

    @Test
    void commitAfterAnAbortIsSurfacedAsAFencingViolationAndNotOverwritten() {
        var f = new Fixture(null);
        f.provider.duringExecute = () -> {
            var current = f.record("k1");
            f.ledger.tryUpdate(current.version(), current.transition(MutationExecutionState.ABORTED,
                    java.time.Instant.now(), current.receipt(), List.of(), "wrongly closed")).orElseThrow();
        };
        var failure = assertThrows(MutationCommittedUnrecordedException.class, () -> f.run("Ada", "k1"));
        assertTrue(failure.getCause().getMessage().contains("fencing"));
        assertEquals(MutationExecutionState.ABORTED, f.record("k1").state());
    }

    @Test
    void auditOutageDuringAProviderFailureStillSurfacesInDoubt() {
        var f = new Fixture(null);
        f.provider.mode = Mode.FAIL_AFTER_COMMIT;
        f.audit.fail = true;
        var failure = assertThrows(MutationInDoubtException.class, () -> f.run("Ada", "k1"));
        assertEquals(1, failure.getSuppressed().length);
    }

    @Test
    void operatorNotAppliedDecisionIsAudited() {
        var f = new Fixture(null);
        f.provider.mode = Mode.FAIL_BEFORE_COMMIT;
        assertThrows(MutationInDoubtException.class, () -> f.run("Ada", "k1"));
        f.recovery().resolve(null, null, "k1", false, "ops-1", "verified absent");
        assertTrue(f.audit.query(null, null).stream()
                .anyMatch(e -> "ops-1".equals(e.actor()) && e.summary().contains("not applied")));
    }
}
