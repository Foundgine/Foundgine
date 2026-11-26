package com.foundgine.runtime.recovery;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.execution.ExecutionReceipt;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.junit.jupiter.api.io.TempDir;

/** The file ledger across real operating-system processes: exclusivity, compare-and-set, and crashed lock holders. */
@Timeout(value = 180, unit = TimeUnit.SECONDS)
class FileMutationExecutionLedgerProcessTest {
    @TempDir Path dir;

    private static final Instant T0 = Instant.parse("2026-01-02T03:04:05Z");
    private static final int PROCESSES = 4, KEYS = 40, UPDATES_EACH = 25;

    static MutationExecutionRecord record(String key, String requestId) {
        var receipt = new ExecutionReceipt(requestId, "pending", "sm-1", 1, 1, 1, 1, "intent", "plan", "auth", "provider",
                T0, T0, List.of(1), List.of("write"), "pending", null, null, null, null, null, null, "corr-1");
        return new MutationExecutionRecord("t", "s", key, requestId, "plan", "corr-1", MutationExecutionState.STARTED, 1,
                0, T0, T0, T0.plusSeconds(60), receipt, List.of(), false, null);
    }

    /** Child-process entry point. Modes: race, hold, write. */
    public static void main(String[] args) throws Exception {
        var ledger = new FileMutationExecutionLedger(args[0]);
        String mode = args[1], id = args[2];
        switch (mode) {
            case "race" -> {
                for (int k = 0; k < KEYS; k++)
                    if (ledger.tryCreate(record("k" + k, "p" + id)).created()) System.out.println("W k" + k);
                int done = 0;
                while (done < UPDATES_EACH) {
                    var cur = ledger.find("t", "s", "shared").orElseThrow();
                    var next = cur.transition(MutationExecutionState.STARTED, T0, cur.receipt(), cur.affectedRows(),
                            "p" + id + "-" + done);
                    if (ledger.tryUpdate(cur.version(), next).isPresent()) done++;
                }
                System.out.println("DONE " + done);
            }
            case "write" -> { // appends until killed by the parent
                for (int i = 0; ; i++) ledger.tryCreate(record("w" + id + "-" + i, "r" + i));
            }
            default -> throw new IllegalArgumentException(mode);
        }
    }

    private Process spawn(String ledgerPath, String mode, String id) throws Exception {
        String java = Path.of(System.getProperty("java.home"), "bin", "java").toString();
        return new ProcessBuilder(java, "-cp", System.getProperty("java.class.path"),
                FileMutationExecutionLedgerProcessTest.class.getName(), ledgerPath, mode, id)
                .redirectErrorStream(true).start();
    }

    private static List<String> lines(Process p) throws Exception {
        var out = new ArrayList<String>();
        try (var r = new BufferedReader(new InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
            for (String l; (l = r.readLine()) != null; ) out.add(l);
        }
        assertTrue(p.waitFor(120, TimeUnit.SECONDS), "child did not finish");
        return out;
    }

    @Test
    void racingProcessesGetOneWinnerPerKeyAndLoseNoUpdates() throws Exception {
        String path = dir.resolve("race.jsonl").toString();
        var ledger = new FileMutationExecutionLedger(path);
        assertTrue(ledger.tryCreate(record("shared", "r0")).created());
        var procs = new ArrayList<Process>();
        for (int i = 0; i < PROCESSES; i++) procs.add(spawn(path, "race", "" + i));
        Map<String, Integer> winners = new HashMap<>();
        for (Process p : procs) {
            var out = lines(p);
            assertEquals(0, p.exitValue(), String.join("\n", out));
            assertTrue(out.contains("DONE " + UPDATES_EACH), String.join("\n", out));
            out.stream().filter(l -> l.startsWith("W ")).forEach(l -> winners.merge(l, 1, Integer::sum));
        }
        assertEquals(KEYS, winners.size());
        winners.forEach((k, n) -> assertEquals(1, n, k + " won more than once"));
        // every compare-and-set update landed exactly once: version 1 + all updates
        var shared = new FileMutationExecutionLedger(path).find("t", "s", "shared").orElseThrow();
        assertEquals(1 + PROCESSES * UPDATES_EACH, shared.version());
    }

    @Test
    void separateInstancesInOneProcessShareTheLockSafely() throws Exception {
        String path = dir.resolve("same-jvm.jsonl").toString();
        var a = new FileMutationExecutionLedger(path);
        var b = new FileMutationExecutionLedger(path);
        var threads = new ArrayList<Thread>();
        var won = new java.util.concurrent.atomic.AtomicInteger();
        for (var l : List.of(a, b))
            threads.add(Thread.ofPlatform().start(() -> {
                for (int k = 0; k < KEYS; k++) if (l.tryCreate(record("k" + k, "r")).created()) won.incrementAndGet();
            }));
        for (var t : threads) t.join();
        assertEquals(KEYS, won.get());
    }

    @Test
    void liveHolderIsNeverEvictedAndADeadHolderBlocksNobody() throws Exception {
        String path = dir.resolve("hold.jsonl").toString();
        var ledger = new FileMutationExecutionLedger(path);
        Path lockFile = Path.of(path + ".lock");
        // a live holder (this JVM, via a second channel) must make the ledger wait, not evict
        try (FileChannel ch = FileChannel.open(lockFile, StandardOpenOption.CREATE, StandardOpenOption.WRITE);
                FileLock held = ch.lock()) {
            Files.setLastModifiedTime(lockFile, java.nio.file.attribute.FileTime.from(Instant.parse("2020-01-01T00:00:00Z")));
            var t = Thread.ofPlatform().start(() -> ledger.tryCreate(record("k", "r")));
            t.join(1500);
            assertTrue(t.isAlive(), "ledger must wait for a live holder even if the lock file looks old");
            assertTrue(held.isValid());
            held.release();
            t.join(30_000);
            assertFalse(t.isAlive());
        }
        assertTrue(ledger.find("t", "s", "k").isPresent());
        // a holder that dies releases the lock with its process: a killed writer blocks nobody
        Process p = spawn(path, "write", "x");
        Thread.sleep(500);
        p.destroyForcibly().waitFor();
        assertTrue(ledger.tryCreate(record("after-kill", "r")).created());
    }

    @Test
    void killingAWriterAtArbitraryPointsLeavesAReadableLedger() throws Exception {
        String path = dir.resolve("kill.jsonl").toString();
        for (int round = 0; round < 5; round++) {
            Process p = spawn(path, "write", "r" + round);
            Thread.sleep(200 + round * 150L);
            p.destroyForcibly().waitFor();
            var reader = new FileMutationExecutionLedger(path); // fails closed on corruption; a torn tail is tolerated
            assertTrue(reader.tryCreate(record("probe" + round, "r")).created());
            assertTrue(reader.find("t", "s", "probe" + round).isPresent());
        }
    }
}
