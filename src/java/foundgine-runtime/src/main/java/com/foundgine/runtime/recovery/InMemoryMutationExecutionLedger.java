package com.foundgine.runtime.recovery;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Optional;

/** Process-local ledger for tests and single-process hosts. Not durable: a restart loses all evidence. */
public class InMemoryMutationExecutionLedger implements IMutationExecutionLedger {
    private final Map<String, MutationExecutionRecord> records = new LinkedHashMap<>();

    @Override
    public synchronized CreateResult tryCreate(MutationExecutionRecord record) {
        Objects.requireNonNull(record, "record");
        var existing = records.get(record.identity());
        if (existing != null) return new CreateResult(false, existing);
        var stored = record.withVersion(1);
        records.put(stored.identity(), stored);
        return new CreateResult(true, stored);
    }

    @Override
    public synchronized Optional<MutationExecutionRecord> tryUpdate(long expectedVersion, MutationExecutionRecord updated) {
        Objects.requireNonNull(updated, "updated");
        var current = records.get(updated.identity());
        if (current == null || current.version() != expectedVersion) return Optional.empty();
        var stored = updated.withVersion(expectedVersion + 1);
        records.put(stored.identity(), stored);
        return Optional.of(stored);
    }

    @Override
    public synchronized Optional<MutationExecutionRecord> find(String tenant, String subject, String idempotencyKey) {
        return Optional.ofNullable(records.get(MutationExecutionRecord.identity(tenant, subject, idempotencyKey)));
    }

    @Override
    public synchronized Optional<MutationExecutionRecord> findByRequestId(String requestId) {
        return records.values().stream().filter(r -> r.requestId().equals(requestId)).findFirst();
    }

    @Override
    public synchronized List<MutationExecutionRecord> listUnresolved(int limit) {
        List<MutationExecutionRecord> result = new ArrayList<>();
        for (var r : records.values()) if (needsAttention(r)) result.add(r);
        result.sort(Comparator.comparing(MutationExecutionRecord::startedAt));
        return result.size() > limit ? new ArrayList<>(result.subList(0, Math.max(0, limit))) : result;
    }

    static boolean needsAttention(MutationExecutionRecord r) {
        return switch (r.state()) {
            case STARTED, IN_DOUBT -> true;
            case COMMITTED -> !r.auditAcknowledged();
            case ABORTED -> false;
        };
    }
}
