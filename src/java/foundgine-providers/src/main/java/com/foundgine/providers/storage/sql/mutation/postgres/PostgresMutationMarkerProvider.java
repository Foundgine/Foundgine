package com.foundgine.providers.storage.sql.mutation.postgres;

import com.foundgine.core.execution.CancellationToken;
import com.foundgine.core.execution.ExecutionContext;
import com.foundgine.core.execution.ExecutionContextKeys;
import com.foundgine.core.execution.mutation.ExecutionMutationIR;
import com.foundgine.core.execution.mutation.IMutationBatchExecutionProvider;
import com.foundgine.core.execution.mutation.IMutationReconciler;
import com.foundgine.core.execution.mutation.IMutationSecurityConformanceEvaluator;
import com.foundgine.core.execution.mutation.MutationBatchResult;
import com.foundgine.core.execution.mutation.MutationNotAppliedException;
import com.foundgine.core.execution.mutation.MutationReconciliationQuery;
import com.foundgine.core.execution.mutation.MutationReconciliationVerdict;
import com.foundgine.core.execution.mutation.MutationSecurityConformanceResult;
import com.foundgine.core.semantic.metadata.IMetadataProvider;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Duration;
import java.util.HexFormat;
import java.util.Objects;
import java.util.function.Function;
import java.util.regex.Pattern;

/**
 * PostgreSQL reference for reconcilable mutations. It owns the transaction, writes a marker row keyed by the engine's
 * execution request id, and runs a non-committing mutation provider inside the same transaction, so the marker is
 * durable exactly when the mutation is.
 *
 * <p>{@link #reconcile} fences by inserting a {@code fenced} row under the same primary key. If the executing
 * transaction already holds the key, PostgreSQL makes that insert wait for it to commit or roll back (up to the lock
 * timeout, after which the answer is "unknown"). If the fence wins, a late execution finds the key taken and cannot
 * commit. That is the fencing contract of {@link MutationReconciliationVerdict}.
 *
 * <p>Outcome rules: a failure before commit that was rolled back is reported as {@link MutationNotAppliedException}.
 * A failure <em>at</em> commit, or a rollback that itself failed, is never reported as not applied, because the
 * database may have committed before the acknowledgement was lost; it surfaces as an ordinary exception and the engine
 * records the execution as in doubt.
 */
public final class PostgresMutationMarkerProvider
        implements IMutationBatchExecutionProvider, IMutationReconciler, IMutationSecurityConformanceEvaluator {

    /** Opens a new connection each time; the provider closes what it opens. */
    @FunctionalInterface
    public interface ConnectionFactory {
        Connection open() throws SQLException;
    }

    private static final Pattern SAFE_TABLE = Pattern.compile("[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)?");
    private static final String UNIQUE_VIOLATION = "23505", LOCK_NOT_AVAILABLE = "55P03";
    private static final String APPLIED = "applied", FENCED = "fenced";

    private final ConnectionFactory connections;
    private final IMetadataProvider metadata;
    private final String table;
    private final Duration lockTimeout;
    private final Function<Connection, IMutationBatchExecutionProvider> delegateFactory;

    public PostgresMutationMarkerProvider(ConnectionFactory connections, IMetadataProvider metadata, String table,
            Duration lockTimeout) {
        this(connections, metadata, table, lockTimeout,
                c -> new PostgresBatchedMutationExecutionProvider(c, metadata, false));
    }

    /** Lets tests and hosts supply the non-committing delegate that runs on the marked transaction's connection. */
    public PostgresMutationMarkerProvider(ConnectionFactory connections, IMetadataProvider metadata, String table,
            Duration lockTimeout, Function<Connection, IMutationBatchExecutionProvider> delegateFactory) {
        this.connections = Objects.requireNonNull(connections, "connections");
        this.metadata = Objects.requireNonNull(metadata, "metadata");
        this.table = Objects.requireNonNull(table, "table");
        if (!SAFE_TABLE.matcher(table).matches())
            throw new IllegalArgumentException("Marker table must be a plain (optionally schema-qualified) identifier.");
        Objects.requireNonNull(lockTimeout, "lockTimeout");
        if (lockTimeout.isNegative() || lockTimeout.isZero() || lockTimeout.compareTo(Duration.ofMinutes(10)) > 0)
            throw new IllegalArgumentException("lockTimeout must be between 1 ms and 10 minutes.");
        this.lockTimeout = lockTimeout;
        this.delegateFactory = Objects.requireNonNull(delegateFactory, "delegateFactory");
    }

    /** Creates the marker table when it does not exist. */
    public void ensureSchema() throws SQLException {
        try (Connection c = connections.open(); Statement s = c.createStatement()) {
            s.execute("CREATE TABLE IF NOT EXISTS " + table
                    + " (request_id text PRIMARY KEY, state text NOT NULL, created_at timestamptz NOT NULL DEFAULT now())");
        }
    }

    @Override
    public MutationSecurityConformanceResult evaluate(ExecutionMutationIR ir) {
        Objects.requireNonNull(ir, "ir");
        // Evaluation only compiles; hand the delegate a connection that fails on any use.
        Connection unusable = (Connection) Proxy.newProxyInstance(Connection.class.getClassLoader(),
                new Class<?>[] {Connection.class}, (proxy, method, args) -> {
                    if (method.getName().equals("toString")) return "unusable-connection";
                    throw new IllegalStateException("Conformance evaluation must not touch the database.");
                });
        var delegate = delegateFactory.apply(unusable);
        if (!(delegate instanceof IMutationSecurityConformanceEvaluator evaluator))
            throw new IllegalStateException("The delegate mutation provider has no security conformance evaluator.");
        var inner = evaluator.evaluate(ir);
        // The gate matches the evidence to this provider's identity, not the delegate's.
        return new MutationSecurityConformanceResult(getClass().getName(), inner.satisfied(), inner.violations());
    }

    @Override
    public MutationBatchResult executeBatch(ExecutionMutationIR ir, ExecutionContext context) {
        return executeBatch(ir, context, CancellationToken.NONE);
    }

    @Override
    public MutationBatchResult executeBatch(ExecutionMutationIR ir, ExecutionContext context,
            CancellationToken token) {
        Objects.requireNonNull(ir, "ir");
        Objects.requireNonNull(context, "context");
        CancellationToken cancellation = token == null ? CancellationToken.NONE : token;
        cancellation.throwIfCancellationRequested();
        Object id = context.tryGetValue(ExecutionContextKeys.EXECUTION_REQUEST_ID).orElse(null);
        String requestId = id instanceof String s && !s.isBlank() ? s : null;
        if (requestId == null) {
            // No marker is possible without a request id: run the delegate in its own transaction, failures unchanged.
            try (Connection c = connections.open()) {
                return new PostgresBatchedMutationExecutionProvider(c, metadata, true)
                        .executeBatch(ir, context, cancellation);
            } catch (SQLException e) {
                throw new IllegalStateException("Unable to open a connection for the mutation.", e);
            }
        }
        return executeInMarkedTransaction(requestId, c -> delegateFactory.apply(c).executeBatch(ir, context, cancellation));
    }

    /**
     * Runs {@code work} and writes the marker in one transaction owned here. {@code work} must not commit or roll
     * back. Throws {@link MutationNotAppliedException} only when nothing committed and the transaction was rolled back
     * (including when a fence already holds the request id).
     */
    public <T> T executeInMarkedTransaction(String requestId, Function<Connection, T> work) {
        if (requestId == null || requestId.isBlank()) throw new IllegalArgumentException("requestId is required.");
        Connection c;
        try {
            c = connections.open();
        } catch (SQLException e) {
            throw new MutationNotAppliedException("No connection could be opened; nothing was executed.", e);
        }
        try (c) {
            T result;
            try {
                c.setAutoCommit(false);
                insertMarker(c, requestId, APPLIED);
                result = work.apply(c);
            } catch (Throwable failure) {
                if (!rolledBack(c)) throw asUnchecked(failure); // cannot prove nothing committed: stay in doubt
                if (isUniqueViolation(failure))
                    throw new MutationNotAppliedException("The request id was fenced; the execution cannot commit.", failure);
                if (failure instanceof MutationNotAppliedException n) throw n;
                throw new MutationNotAppliedException("The mutation failed and its transaction was rolled back.", failure);
            }
            try {
                c.commit();
            } catch (SQLException e) {
                // The database may have committed before the acknowledgement was lost: never "not applied".
                throw new IllegalStateException("Commit failed; the outcome is unknown.", e);
            }
            return result;
        } catch (SQLException closeFailure) {
            // Closing after a successful commit changes nothing about the outcome.
            throw new IllegalStateException("Closing the connection failed.", closeFailure);
        }
    }

    @Override
    public MutationReconciliationVerdict reconcile(MutationReconciliationQuery query) {
        Objects.requireNonNull(query, "query");
        if (query.requestId() == null || query.requestId().isBlank())
            return MutationReconciliationVerdict.unknown("The execution has no request id.");
        try (Connection c = connections.open()) {
            c.setAutoCommit(false);
            try (Statement s = c.createStatement()) {
                s.execute("SET LOCAL lock_timeout = '" + lockTimeout.toMillis() + "ms'");
            }
            boolean fenced;
            try (PreparedStatement p = c.prepareStatement(
                    "INSERT INTO " + table + " (request_id, state) VALUES (?, ?) ON CONFLICT (request_id) DO NOTHING")) {
                p.setString(1, query.requestId());
                p.setString(2, FENCED);
                fenced = p.executeUpdate() == 1; // waits for an open execution holding this key
            }
            if (fenced) {
                c.commit();
                return MutationReconciliationVerdict.notApplied("Fenced; a late execution can no longer commit.");
            }
            String state;
            try (PreparedStatement p = c.prepareStatement("SELECT state FROM " + table + " WHERE request_id = ?")) {
                p.setString(1, query.requestId());
                try (ResultSet rs = p.executeQuery()) {
                    state = rs.next() ? rs.getString(1) : null;
                }
            }
            c.rollback();
            if (APPLIED.equals(state)) return MutationReconciliationVerdict.applied(evidence(query));
            if (FENCED.equals(state)) return MutationReconciliationVerdict.notApplied("Already fenced.");
            return MutationReconciliationVerdict.unknown("The marker row is in an unexpected state.");
        } catch (SQLException e) {
            if (LOCK_NOT_AVAILABLE.equals(e.getSQLState()))
                return MutationReconciliationVerdict.unknown("The execution is still open; try again later.");
            return MutationReconciliationVerdict.unknown("Reconciliation failed: " + e.getSQLState());
        }
    }

    private void insertMarker(Connection c, String requestId, String state) throws SQLException {
        try (Statement s = c.createStatement()) {
            s.execute("SET LOCAL lock_timeout = '" + lockTimeout.toMillis() + "ms'");
        }
        try (PreparedStatement p = c.prepareStatement("INSERT INTO " + table + " (request_id, state) VALUES (?, ?)")) {
            p.setString(1, requestId);
            p.setString(2, state);
            p.executeUpdate();
        }
    }

    private static boolean rolledBack(Connection c) {
        try {
            c.rollback();
            return true;
        } catch (SQLException e) {
            return false;
        }
    }

    private static boolean isUniqueViolation(Throwable t) {
        for (Throwable x = t; x != null; x = x.getCause())
            if (x instanceof SQLException e && UNIQUE_VIOLATION.equals(e.getSQLState())) return true;
        return false;
    }

    private static RuntimeException asUnchecked(Throwable t) {
        if (t instanceof InvocationTargetException i && i.getCause() != null) return asUnchecked(i.getCause());
        if (t instanceof RuntimeException r && !(r instanceof MutationNotAppliedException)) return r;
        return new IllegalStateException("The mutation failed and could not be rolled back.", t);
    }

    private static String evidence(MutationReconciliationQuery q) {
        try {
            String text = q.requestId() + "|" + q.idempotencyKey() + "|" + q.planFingerprint() + "|" + APPLIED;
            return HexFormat.of().formatHex(
                    MessageDigest.getInstance("SHA-256").digest(text.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }
}
