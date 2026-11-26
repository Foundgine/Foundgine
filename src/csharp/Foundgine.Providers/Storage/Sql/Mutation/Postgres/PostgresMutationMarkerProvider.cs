using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Foundgine.Core.Execution;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Core.Semantic.Metadata;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;

namespace Foundgine.Providers.Storage.Sql.Mutation.Postgres;

/// <summary>
/// PostgreSQL reference for reconcilable mutations. It owns the transaction, writes a marker row keyed by the engine's
/// execution request id, and runs a non-committing mutation provider inside the same transaction, so the marker is
/// durable exactly when the mutation is. Port of the Java <c>PostgresMutationMarkerProvider</c>.
/// </summary>
/// <remarks>
/// <para><see cref="ReconcileAsync"/> fences by inserting a <c>fenced</c> row under the same primary key. If the
/// executing transaction already holds the key, PostgreSQL makes that insert wait for it to commit or roll back (up to
/// the lock timeout, after which the answer is "unknown"). If the fence wins, a late execution finds the key taken and
/// cannot commit. That is the fencing contract of <see cref="MutationReconciliationVerdict"/>. Marker rows are that
/// evidence: do not delete one while its execution could still run or be reconciled.</para>
/// <para>A failure before commit that was rolled back is reported as <see cref="MutationNotAppliedException"/>. A
/// failure at commit, or a rollback that itself failed, is never reported as not applied, because the database may have
/// committed before the acknowledgement was lost; the engine then records the execution as in doubt.</para>
/// <para>The delegate and the work passed to <see cref="ExecuteInMarkedTransaction{T}"/> must use the supplied
/// transaction and must not commit or roll it back.</para>
/// </remarks>
public sealed class PostgresMutationMarkerProvider :
    IMutationBatchExecutionProvider, IMutationReconciler, IMutationSecurityConformanceEvaluator
{
    private const string UniqueViolation = "23505";
    private const string LockNotAvailable = "55P03";
    private const string Applied = "applied";
    private const string Fenced = "fenced";

    private static readonly Regex SafeTable = new(
        @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?\z", RegexOptions.CultureInvariant);

    private readonly Func<DbConnection> _connections;
    private readonly IMetadataProvider _metadata;
    private readonly string _table;
    private readonly long _lockTimeoutMs;
    private readonly Func<DbConnection, DbTransaction?, IMutationBatchExecutionProvider> _delegateFactory;

    /// <summary>Creates the provider.</summary>
    /// <remarks>
    /// <c>connectionFactory</c> returns a new connection on each call (open or closed); the provider opens it when
    /// needed and disposes it. <c>table</c> is a plain, optionally schema-qualified identifier. <c>lockTimeout</c> is
    /// between 1 ms and 10 minutes. <c>delegateFactory</c> defaults to
    /// <see cref="PostgresBatchedMutationExecutionProvider"/> enlisted in the supplied transaction; it is called with
    /// a null transaction, and a connection that must not be used, for conformance evaluation.
    /// </remarks>
    public PostgresMutationMarkerProvider(
        Func<DbConnection> connectionFactory,
        IMetadataProvider metadata,
        string table,
        TimeSpan lockTimeout,
        Func<DbConnection, DbTransaction?, IMutationBatchExecutionProvider>? delegateFactory = null)
    {
        _connections = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _table = table ?? throw new ArgumentNullException(nameof(table));
        if (!SafeTable.IsMatch(table))
            throw new ArgumentException(
                "Marker table must be a plain (optionally schema-qualified) identifier.", nameof(table));

        // PostgreSQL reads lock_timeout = 0 as "no timeout", so a sub-millisecond value must be rejected.
        if (lockTimeout < TimeSpan.FromMilliseconds(1) || lockTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), "lockTimeout must be between 1 ms and 10 minutes.");

        _lockTimeoutMs = (long)lockTimeout.TotalMilliseconds;
        _delegateFactory = delegateFactory
            ?? ((connection, transaction) =>
                new PostgresBatchedMutationExecutionProvider(connection, metadata, transaction));
    }

    /// <summary>Creates the marker table when it does not exist.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connections();
        await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS " + _table +
            " (request_id text PRIMARY KEY, state text NOT NULL, created_at timestamptz NOT NULL DEFAULT now())";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public MutationSecurityConformanceResult Evaluate(ExecutionMutationIR ir)
    {
        ArgumentNullException.ThrowIfNull(ir);

        // Evaluation only compiles; the delegate gets a connection that fails on use.
        using var unusable = new UnusableConnection();
        if (_delegateFactory(unusable, null) is not IMutationSecurityConformanceEvaluator evaluator)
            throw new InvalidOperationException(
                "The delegate mutation provider has no security conformance evaluator.");

        var inner = evaluator.Evaluate(ir);

        // The gate matches the evidence to this provider's identity, not the delegate's.
        return new MutationSecurityConformanceResult(
            GetType().FullName ?? GetType().Name, inner.Satisfied, inner.Violations);
    }

    public MutationBatchResult ExecuteBatch(ExecutionMutationIR ir, ExecutionContext context) =>
        ExecuteBatch(ir, context, CancellationToken.None);

    public MutationBatchResult ExecuteBatch(
        ExecutionMutationIR ir, ExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ir);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        context.TryGetValue(ExecutionContextKeys.ExecutionRequestId, out var id);
        if (id is not string requestId || string.IsNullOrWhiteSpace(requestId))
        {
            // No marker is possible without a request id: the batched provider owns the transaction, failures unchanged.
            using var connection = _connections();
            Open(connection);
            return new PostgresBatchedMutationExecutionProvider(connection, _metadata)
                .ExecuteBatch(ir, context, cancellationToken);
        }

        return ExecuteInMarkedTransaction(requestId, (connection, transaction) =>
        {
            var inner = _delegateFactory(connection, transaction);
            var result = inner.ExecuteBatch(ir, context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        });
    }

    /// <summary>Runs <c>work</c> and writes the marker in one transaction owned here.</summary>
    /// <remarks>
    /// Throws <see cref="MutationNotAppliedException"/> only when nothing committed: no connection could be opened, or
    /// the transaction was rolled back (including when a fence already holds the request id).
    /// </remarks>
    public T ExecuteInMarkedTransaction<T>(string requestId, Func<DbConnection, DbTransaction, T> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(work);

        DbConnection? connection = null;
        DbTransaction? transaction = null;
        try
        {
            try
            {
                connection = _connections();
                Open(connection);
                transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            }
            catch (Exception failure)
            {
                throw new MutationNotAppliedException("No transaction could be started; nothing was executed.", failure);
            }

            var markerWritten = false;
            T result;
            try
            {
                SetLockTimeout(connection, transaction);
                InsertAppliedMarker(connection, transaction, requestId);
                markerWritten = true;
                result = work(connection, transaction);

                // The marker must still be visible in a healthy transaction. This fails if the work swallowed a
                // statement error, which leaves a PostgreSQL transaction able only to roll back.
                VerifyMarker(connection, transaction, requestId);
            }
            catch (Exception failure)
            {
                if (!TryRollBack(transaction))
                {
                    // Cannot prove nothing committed: never surface this as "not applied".
                    if (failure is MutationNotAppliedException)
                        throw new InvalidOperationException(
                            "The mutation failed and could not be rolled back; the outcome is unknown.", failure);
                    throw;
                }

                if (failure is MutationNotAppliedException) throw;
                if (!markerWritten && failure is DbException { SqlState: UniqueViolation })
                    throw new MutationNotAppliedException(
                        "The request id is already taken (fenced or applied); this execution cannot commit.", failure);
                throw new MutationNotAppliedException("The mutation failed and its transaction was rolled back.", failure);
            }

            try
            {
                transaction.Commit();
            }
            catch (Exception failure)
            {
                // The database may have committed before the acknowledgement was lost: never "not applied".
                throw new InvalidOperationException("Commit failed; the outcome is unknown.", failure);
            }

            return result;
        }
        finally
        {
            // A failure while disposing changes nothing about the outcome and must not replace it.
            DisposeQuietly(transaction);
            DisposeQuietly(connection);
        }
    }

    public async ValueTask<MutationReconciliationVerdict> ReconcileAsync(
        MutationReconciliationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query.RequestId))
            return MutationReconciliationVerdict.Unknown("The execution has no request id.");

        try
        {
            await using var connection = _connections();
            await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            await using (var timeout = Command(connection, transaction, LockTimeoutSql()))
                await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using (var fence = Command(connection, transaction,
                "INSERT INTO " + _table + " (request_id, state) VALUES (@request_id, @state) " +
                "ON CONFLICT (request_id) DO NOTHING"))
            {
                AddText(fence, "request_id", query.RequestId);
                AddText(fence, "state", Fenced);
                // Waits for an open execution holding this key.
                if (await fence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return MutationReconciliationVerdict.NotApplied("Fenced; a late execution can no longer commit.");
                }
            }

            string? state;
            await using (var read = Command(connection, transaction, SelectStateSql()))
            {
                AddText(read, "request_id", query.RequestId);
                state = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            }

            // Disposing the uncommitted transaction rolls it back.
            return state switch
            {
                Applied => MutationReconciliationVerdict.Applied(Evidence(query)),
                Fenced => MutationReconciliationVerdict.NotApplied("Already fenced."),
                _ => MutationReconciliationVerdict.Unknown("The marker row is in an unexpected state.")
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (DbException failure) when (failure.SqlState == LockNotAvailable)
        {
            return MutationReconciliationVerdict.Unknown("The execution is still open; try again later.");
        }
        catch (Exception failure)
        {
            var code = (failure as DbException)?.SqlState ?? failure.GetType().Name;
            return MutationReconciliationVerdict.Unknown("Reconciliation failed: " + code);
        }
    }

    private string LockTimeoutSql() => "SET LOCAL lock_timeout = '" + _lockTimeoutMs + "ms'";

    private string SelectStateSql() => "SELECT state FROM " + _table + " WHERE request_id = @request_id";

    private void SetLockTimeout(DbConnection connection, DbTransaction transaction)
    {
        using var command = Command(connection, transaction, LockTimeoutSql());
        command.ExecuteNonQuery();
    }

    private void InsertAppliedMarker(DbConnection connection, DbTransaction transaction, string requestId)
    {
        using var command = Command(connection, transaction,
            "INSERT INTO " + _table + " (request_id, state) VALUES (@request_id, @state)");
        AddText(command, "request_id", requestId);
        AddText(command, "state", Applied);
        command.ExecuteNonQuery();
    }

    private void VerifyMarker(DbConnection connection, DbTransaction transaction, string requestId)
    {
        using var command = Command(connection, transaction, SelectStateSql());
        AddText(command, "request_id", requestId);
        if (command.ExecuteScalar() as string != Applied)
            throw new InvalidOperationException("The marker row is no longer part of the transaction.");
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddText(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void Open(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open) connection.Open();
    }

    private static async Task OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryRollBack(DbTransaction transaction)
    {
        try
        {
            transaction.Rollback();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DisposeQuietly(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception)
        {
            // The outcome was decided by the commit or rollback.
        }
    }

    private static string Evidence(MutationReconciliationQuery query)
    {
        var text = query.RequestId + "|" + query.IdempotencyKey + "|" + query.PlanFingerprint + "|" + Applied;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Stands in for a connection during conformance evaluation; any attempt to reach a database throws.</summary>
    private sealed class UnusableConnection : DbConnection
    {
        private static InvalidOperationException Refused() =>
            new("Conformance evaluation must not touch the database.");

        [AllowNull]
        public override string ConnectionString
        {
            get => string.Empty;
            set => throw Refused();
        }

        public override string Database => throw Refused();
        public override string DataSource => throw Refused();
        public override string ServerVersion => throw Refused();
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw Refused();
        public override void Close() { }
        public override void Open() => throw Refused();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw Refused();
        protected override DbCommand CreateDbCommand() => throw Refused();
    }
}
