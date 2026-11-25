package com.foundgine.runtime.recovery;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.execution.ExecutionReceipt;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

class FileMutationExecutionLedgerTest {
    @TempDir Path dir;

    private static final Instant T0 = Instant.parse("2026-01-02T03:04:05.1234567Z");

    private static ExecutionReceipt receipt(String requestId, String status) {
        return new ExecutionReceipt(requestId, status, "sm-1", 1, 1, 1, 1, "intent-fp", "plan-fp", "auth-fp",
                "provider", T0, T0, List.of(1, 2), List.of("write"), "result-fp", null, null, null, null, null,
                null, "corr-1");
    }

    private static MutationExecutionRecord started(String key, String requestId) {
        return new MutationExecutionRecord("t1", "s1", key, requestId, "plan-fp", "corr-1",
                MutationExecutionState.STARTED, 1, 0, T0, T0, T0.plusSeconds(60), receipt(requestId, "pending"),
                List.of(), false, null);
    }

    @Test
    void jsonRoundTripPreservesRecordAtTickPrecision() {
        var r = started("k1", "r1").withVersion(3);
        var back = MutationExecutionRecordJson.fromJson(MutationExecutionRecordJson.toJson(r));
        assertEquals(r, back);
        assertTrue(MutationExecutionRecordJson.toJson(r).contains("\"startedAtUtc\":\"2026-01-02T03:04:05.1234567Z\""));
    }

    @Test
    void createIsExclusiveAndUpdateIsCompareAndSet() {
        var ledger = new FileMutationExecutionLedger(dir.resolve("l.jsonl").toString());
        var c1 = ledger.tryCreate(started("k1", "r1"));
        assertTrue(c1.created());
        assertEquals(1, c1.record().version());
        var c2 = ledger.tryCreate(started("k1", "r2"));
        assertFalse(c2.created());
        assertEquals("r1", c2.record().requestId());

        var next = c1.record().transition(MutationExecutionState.COMMITTED, T0.plusSeconds(1),
                receipt("r1", "succeeded"), List.of(1), "done");
        assertTrue(ledger.tryUpdate(99, next).isEmpty());
        var stored = ledger.tryUpdate(1, next).orElseThrow();
        assertEquals(2, stored.version());
        assertTrue(ledger.tryUpdate(1, next).isEmpty());
    }

    @Test
    void evidenceSurvivesRestartAndIsSharedAcrossInstances() {
        String p = dir.resolve("l.jsonl").toString();
        var a = new FileMutationExecutionLedger(p);
        a.tryCreate(started("k1", "r1"));
        var b = new FileMutationExecutionLedger(p);
        assertEquals("r1", b.find("t1", "s1", "k1").orElseThrow().requestId());
        b.tryCreate(started("k2", "r2"));
        assertEquals("k2", a.findByRequestId("r2").orElseThrow().idempotencyKey());
        assertEquals(2, a.listUnresolved(10).size());
        assertEquals(1, a.listUnresolved(1).size());
    }

    @Test
    void tornTailIsIgnoredAndOverwrittenByNextAppend() throws Exception {
        Path f = dir.resolve("l.jsonl");
        var a = new FileMutationExecutionLedger(f.toString());
        a.tryCreate(started("k1", "r1"));
        Files.write(f, "{\"format\":\"foundgine-mutation-ledger-v1\",\"ten".getBytes(StandardCharsets.UTF_8),
                StandardOpenOption.APPEND);
        var b = new FileMutationExecutionLedger(f.toString());
        assertTrue(b.find("t1", "s1", "k1").isPresent());
        assertTrue(b.tryCreate(started("k2", "r2")).created());
        var c = new FileMutationExecutionLedger(f.toString());
        assertTrue(c.find("t1", "s1", "k2").isPresent());
    }

    @Test
    void corruptCompleteLineFailsClosed() throws Exception {
        Path f = dir.resolve("l.jsonl");
        Files.write(f, "garbage\n".getBytes(StandardCharsets.UTF_8));
        var ledger = new FileMutationExecutionLedger(f.toString());
        assertThrows(MutationLedgerException.class, () -> ledger.find("t1", "s1", "k1"));
    }

    @Test
    void identityDistinguishesNullFromEmptyScope() {
        assertNotEquals(MutationExecutionRecord.identity(null, null, "k"), MutationExecutionRecord.identity("", "", "k"));
    }

    @Test
    void invalidIdempotencyKeyIsRejected() {
        assertThrows(IllegalArgumentException.class, () -> started("bad key!", "r1"));
    }
}
