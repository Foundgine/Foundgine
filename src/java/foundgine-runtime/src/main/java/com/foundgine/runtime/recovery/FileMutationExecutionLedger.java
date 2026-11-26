package com.foundgine.runtime.recovery;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.channels.OverlappingFileLockException;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Optional;

/**
 * Durable ledger backed by a lock-protected, append-only JSON-lines journal ({@link MutationExecutionRecordJson}); the
 * latest line for an identity wins. Every append is forced to disk before the call returns, so intent recorded before a
 * crash is still there after restart. Each operation takes the file lock and reads any lines other processes appended.
 *
 * <p>Single-node / shared-filesystem use, like {@code FileSecurityWarrantReplayStore}; cloud deployments should implement
 * {@link IMutationExecutionLedger} over a shared transactional store. The journal is never compacted by this class
 * (archive terminal records out of band). A torn final line (crash mid-append) is ignored and truncated by the next
 * append; any other unreadable line fails closed with {@link MutationLedgerException}.
 */
public final class FileMutationExecutionLedger implements IMutationExecutionLedger {
    private static final Duration LOCK_TIMEOUT = Duration.ofSeconds(30);

    private final Path path, lockPath;
    private final Object gate = new Object();
    private final Map<String, MutationExecutionRecord> records = new LinkedHashMap<>();
    private long parsedOffset;

    public FileMutationExecutionLedger(String value) {
        if (value == null || value.isBlank()) throw new IllegalArgumentException("A ledger path is required.");
        path = Path.of(value).toAbsolutePath();
        lockPath = Path.of(path + ".lock");
        try {
            Path parent = path.getParent();
            if (parent != null) Files.createDirectories(parent);
            try {
                Files.createFile(path);
            } catch (FileAlreadyExistsException ignored) {
            }
        } catch (IOException e) {
            throw new MutationLedgerException("Unable to initialize the mutation execution ledger.", e);
        }
    }

    @Override
    public CreateResult tryCreate(MutationExecutionRecord record) {
        Objects.requireNonNull(record, "record");
        return locked(() -> {
            var existing = records.get(record.identity());
            if (existing != null) return new CreateResult(false, existing);
            var stored = record.withVersion(1);
            append(stored);
            records.put(stored.identity(), stored);
            return new CreateResult(true, stored);
        });
    }

    @Override
    public Optional<MutationExecutionRecord> tryUpdate(long expectedVersion, MutationExecutionRecord updated) {
        Objects.requireNonNull(updated, "updated");
        return locked(() -> {
            var current = records.get(updated.identity());
            if (current == null || current.version() != expectedVersion) return Optional.empty();
            var stored = updated.withVersion(expectedVersion + 1);
            append(stored);
            records.put(stored.identity(), stored);
            return Optional.of(stored);
        });
    }

    @Override
    public Optional<MutationExecutionRecord> find(String tenant, String subject, String idempotencyKey) {
        return locked(() -> Optional.ofNullable(
                records.get(MutationExecutionRecord.identity(tenant, subject, idempotencyKey))));
    }

    @Override
    public Optional<MutationExecutionRecord> findByRequestId(String requestId) {
        return locked(() -> records.values().stream().filter(r -> r.requestId().equals(requestId)).findFirst());
    }

    @Override
    public List<MutationExecutionRecord> listUnresolved(int limit) {
        return locked(() -> {
            List<MutationExecutionRecord> result = new ArrayList<>();
            for (var r : records.values()) if (InMemoryMutationExecutionLedger.needsAttention(r)) result.add(r);
            result.sort(Comparator.comparing(MutationExecutionRecord::startedAt));
            return result.size() > limit ? new ArrayList<>(result.subList(0, Math.max(0, limit))) : result;
        });
    }

    private interface IoAction<T> { T run() throws IOException; }

    private <T> T locked(IoAction<T> action) {
        synchronized (gate) {
            try (var lock = acquireLock()) {
                refresh();
                return action.run();
            } catch (IOException e) {
                throw new MutationLedgerException("The mutation execution ledger is unavailable.", e);
            }
        }
    }

    /** Parses journal bytes appended since the last refresh; an unterminated tail is left for {@link #append}. */
    private void refresh() throws IOException {
        long size = Files.size(path);
        if (size < parsedOffset) {
            records.clear();
            parsedOffset = 0;
        }
        if (size == parsedOffset) return;
        byte[] bytes = new byte[(int) (size - parsedOffset)];
        try (FileChannel ch = FileChannel.open(path, StandardOpenOption.READ)) {
            ch.position(parsedOffset);
            ByteBuffer buf = ByteBuffer.wrap(bytes);
            while (buf.hasRemaining() && ch.read(buf) >= 0) { }
        }
        int start = 0;
        for (int i = 0; i < bytes.length; i++) {
            if (bytes[i] != '\n') continue;
            String line = new String(bytes, start, i - start, StandardCharsets.UTF_8);
            if (!line.isBlank()) {
                try {
                    var rec = MutationExecutionRecordJson.fromJson(line);
                    var prior = records.get(rec.identity());
                    if (prior == null || rec.version() >= prior.version()) records.put(rec.identity(), rec);
                } catch (RuntimeException e) {
                    throw new MutationLedgerException(
                            "Corrupt mutation ledger journal at byte offset " + (parsedOffset + start) + ".", e);
                }
            }
            start = i + 1;
        }
        parsedOffset += start;
    }

    private void append(MutationExecutionRecord record) throws IOException {
        byte[] bytes = (MutationExecutionRecordJson.toJson(record) + "\n").getBytes(StandardCharsets.UTF_8);
        try (FileChannel ch = FileChannel.open(path, StandardOpenOption.WRITE)) {
            if (ch.size() > parsedOffset) ch.truncate(parsedOffset); // torn tail from a crashed append
            ch.position(parsedOffset);
            ByteBuffer buf = ByteBuffer.wrap(bytes);
            while (buf.hasRemaining()) ch.write(buf);
            ch.force(true);
        }
        parsedOffset += bytes.length;
    }

    /**
     * Takes an operating-system advisory lock on the sidecar lock file. The OS releases it when the holder dies, so a
     * crashed process never leaves a stale lock and a live holder is never evicted. The lock file itself is never
     * deleted (deleting it would let two processes lock different inodes).
     */
    private Lock acquireLock() throws IOException {
        Instant start = Instant.now();
        FileChannel channel = FileChannel.open(lockPath, StandardOpenOption.CREATE, StandardOpenOption.WRITE);
        try {
            while (true) {
                try {
                    FileLock acquired = channel.tryLock();
                    if (acquired != null) return new Lock(channel, acquired);
                } catch (OverlappingFileLockException held) {
                    // another ledger instance in this JVM holds it; wait like any other holder
                }
                if (Duration.between(start, Instant.now()).compareTo(LOCK_TIMEOUT) >= 0)
                    throw new IOException("Timed out acquiring the ledger lock '" + lockPath + "'.");
                try {
                    Thread.sleep(10);
                } catch (InterruptedException x) {
                    Thread.currentThread().interrupt();
                    throw new IOException("Interrupted while acquiring the ledger lock.", x);
                }
            }
        } catch (IOException | RuntimeException e) {
            try { channel.close(); } catch (IOException ignored) { }
            throw e;
        }
    }

    private static final class Lock implements AutoCloseable {
        private final FileChannel channel;
        private final FileLock lock;

        Lock(FileChannel c, FileLock l) { channel = c; lock = l; }

        @Override
        public void close() throws IOException {
            try { lock.release(); } finally { channel.close(); }
        }
    }
}
