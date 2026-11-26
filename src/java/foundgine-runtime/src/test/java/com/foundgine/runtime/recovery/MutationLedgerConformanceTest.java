package com.foundgine.runtime.recovery;

import static org.junit.jupiter.api.Assertions.*;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * Cross-language conformance: the journal in src/conformance/mutation-ledger-journal.jsonl is the
 * {@code foundgine-mutation-ledger-v1} form every runtime must read and rewrite byte for byte. Set
 * {@code foundgine.conformance.dir} to override the default {@code ../../conformance}.
 */
class MutationLedgerConformanceTest {
    @TempDir Path dir;

    private static File fixture(String name) {
        String d = System.getProperty("foundgine.conformance.dir");
        return new File(d != null ? d : "../../conformance", name);
    }

    private static JsonNode scenarios() throws Exception {
        return new ObjectMapper().readTree(fixture("mutation-ledger-scenarios.json"));
    }

    private Path copyJournal() throws Exception {
        Path p = dir.resolve("journal.jsonl");
        Files.copy(fixture("mutation-ledger-journal.jsonl").toPath(), p);
        return p;
    }

    @Test
    void everyJournalLineRoundTripsByteForByte() throws Exception {
        List<String> lines = Files.readAllLines(fixture("mutation-ledger-journal.jsonl").toPath(), StandardCharsets.UTF_8);
        assertFalse(lines.isEmpty());
        for (String line : lines)
            assertEquals(line, MutationExecutionRecordJson.toJson(MutationExecutionRecordJson.fromJson(line)));
    }

    @Test
    void ledgerReadsTheJournalAndLatestVersionWins() throws Exception {
        var ledger = new FileMutationExecutionLedger(copyJournal().toString());
        JsonNode s = scenarios();
        for (JsonNode e : s.get("finalRecords")) {
            String tenant = e.get("tenant").isNull() ? null : e.get("tenant").asText();
            String subject = e.get("subject").isNull() ? null : e.get("subject").asText();
            var r = ledger.find(tenant, subject, e.get("idempotencyKey").asText()).orElseThrow();
            assertEquals(e.get("requestId").asText(), r.requestId());
            assertEquals(e.get("state").asText(), r.state().name());
            assertEquals(e.get("version").asLong(), r.version());
            assertEquals(e.get("resolution").isNull() ? null : e.get("resolution").asText(), r.resolution());
            assertEquals(e.get("affectedRows").size(), r.affectedRows().size());
        }
        var unresolved = ledger.listUnresolved(100).stream().map(MutationExecutionRecord::idempotencyKey).toList();
        var expected = new java.util.ArrayList<String>();
        s.get("unresolved").forEach(x -> expected.add(x.asText()));
        assertEquals(expected, unresolved);
    }

    @Test
    void tornTailIsIgnoredAndCorruptLineFailsClosed() throws Exception {
        JsonNode s = scenarios();
        Path torn = copyJournal();
        Files.writeString(torn, s.get("tornTail").get("append").asText(), StandardCharsets.UTF_8, StandardOpenOption.APPEND);
        var ledger = new FileMutationExecutionLedger(torn.toString());
        assertEquals(s.get("finalRecords").size(), countFinal(ledger, s));

        Path corrupt = dir.resolve("corrupt.jsonl");
        List<String> lines = Files.readAllLines(fixture("mutation-ledger-journal.jsonl").toPath(), StandardCharsets.UTF_8);
        lines.set(s.get("corruptLine").get("replaceLine").asInt(), s.get("corruptLine").get("with").asText());
        Files.write(corrupt, lines, StandardCharsets.UTF_8);
        var bad = new FileMutationExecutionLedger(corrupt.toString());
        assertThrows(MutationLedgerException.class, () -> bad.find("t1", "s1", "k-started"));
    }

    private static int countFinal(FileMutationExecutionLedger ledger, JsonNode s) {
        int n = 0;
        for (JsonNode e : s.get("finalRecords")) {
            String tenant = e.get("tenant").isNull() ? null : e.get("tenant").asText();
            String subject = e.get("subject").isNull() ? null : e.get("subject").asText();
            if (ledger.find(tenant, subject, e.get("idempotencyKey").asText()).isPresent()) n++;
        }
        return n;
    }
}
