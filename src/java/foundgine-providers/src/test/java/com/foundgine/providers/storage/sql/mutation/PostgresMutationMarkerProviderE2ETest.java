package com.foundgine.providers.storage.sql.mutation;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.execution.mutation.MutationNotAppliedException;
import com.foundgine.core.execution.mutation.MutationReconciliationQuery;
import com.foundgine.core.execution.mutation.MutationReconciliationVerdict.Outcome;
import com.foundgine.providers.storage.sql.mutation.postgres.PostgresMutationMarkerProvider;
import java.sql.DriverManager;
import java.time.Duration;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Assumptions;
import org.junit.jupiter.api.Test;

/** Opt-in: set FOUNDGINE_POSTGRES_CONNECTION_STRING to a JDBC URL to run. Uses a throwaway marker table. */
class PostgresMutationMarkerProviderE2ETest {
    private static String url() {
        String v = System.getenv("FOUNDGINE_POSTGRES_CONNECTION_STRING");
        Assumptions.assumeTrue(v != null && v.startsWith("jdbc:"), "Set FOUNDGINE_POSTGRES_CONNECTION_STRING.");
        return v;
    }

    private static MutationReconciliationQuery query(String id) {
        return new MutationReconciliationQuery(id, "t", "s", "k", "plan", "corr", Instant.now());
    }

    private static PostgresMutationMarkerProvider provider(String url, Duration timeout) throws Exception {
        var p = new PostgresMutationMarkerProvider(() -> DriverManager.getConnection(url), meta(),
                "fg_marker_test", timeout, c -> null);
        p.ensureSchema();
        return p;
    }

    private static com.foundgine.core.semantic.metadata.IMetadataProvider meta() {
        return (com.foundgine.core.semantic.metadata.IMetadataProvider) java.lang.reflect.Proxy.newProxyInstance(
                PostgresMutationMarkerProviderE2ETest.class.getClassLoader(),
                new Class<?>[] {com.foundgine.core.semantic.metadata.IMetadataProvider.class},
                (proxy, method, args) -> { throw new UnsupportedOperationException(); });
    }

    @Test
    void committedExecutionReconcilesAsApplied() throws Exception {
        String url = url();
        var p = provider(url, Duration.ofSeconds(5));
        String id = UUID.randomUUID().toString();
        assertEquals("done", p.executeInMarkedTransaction(id, c -> "done"));
        assertEquals(Outcome.APPLIED, p.reconcile(query(id)).outcome());
    }

    @Test
    void rolledBackFailureIsNotAppliedAndLeavesNoMarker() throws Exception {
        String url = url();
        var p = provider(url, Duration.ofSeconds(5));
        String id = UUID.randomUUID().toString();
        assertThrows(MutationNotAppliedException.class, () -> p.executeInMarkedTransaction(id, c -> {
            throw new IllegalStateException("boom");
        }));
        // no marker: reconcile fences it, which is the binding "not applied" answer
        assertEquals(Outcome.NOT_APPLIED, p.reconcile(query(id)).outcome());
    }

    @Test
    void fenceStopsALateExecution() throws Exception {
        String url = url();
        var p = provider(url, Duration.ofSeconds(5));
        String id = UUID.randomUUID().toString();
        assertEquals(Outcome.NOT_APPLIED, p.reconcile(query(id)).outcome());
        assertThrows(MutationNotAppliedException.class, () -> p.executeInMarkedTransaction(id, c -> "late"));
    }

    @Test
    void reconcileCannotDecideWhileTheExecutionIsOpenThenWaitsForItsCommit() throws Exception {
        String url = url();
        var shortWait = provider(url, Duration.ofMillis(300));
        var longWait = provider(url, Duration.ofSeconds(20));
        String id = UUID.randomUUID().toString();
        var started = new CompletableFuture<Void>();
        var release = new CompletableFuture<Void>();
        var exec = CompletableFuture.runAsync(() -> longWait.executeInMarkedTransaction(id, c -> {
            started.complete(null);
            release.join();
            return "ok";
        }));
        started.get(10, TimeUnit.SECONDS);
        assertEquals(Outcome.UNKNOWN, shortWait.reconcile(query(id)).outcome());
        var waiting = CompletableFuture.supplyAsync(() -> longWait.reconcile(query(id)).outcome());
        Thread.sleep(500);
        release.complete(null);
        exec.get(10, TimeUnit.SECONDS);
        assertEquals(Outcome.APPLIED, waiting.get(10, TimeUnit.SECONDS));
    }

    @Test
    void unsafeTableNamesAndTimeoutsAreRejected() {
        assertThrows(IllegalArgumentException.class, () -> new PostgresMutationMarkerProvider(
                () -> null, meta(), "t; DROP TABLE x", Duration.ofSeconds(1)));
        assertThrows(IllegalArgumentException.class, () -> new PostgresMutationMarkerProvider(
                () -> null, meta(), "ok", Duration.ZERO));
    }
}
