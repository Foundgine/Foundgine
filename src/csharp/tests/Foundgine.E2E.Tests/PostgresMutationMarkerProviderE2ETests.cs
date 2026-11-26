using System.Data.Common;
using Foundgine.Core.Abstractions;
using Foundgine.Core.Execution;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Core.Semantic.Metadata;
using Foundgine.Core.Semantic.Planning.Mutation;
using Foundgine.Core.Semantic.Query;
using Foundgine.Providers.Storage.Sql.Mutation.Postgres;
using Npgsql;
using Xunit;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;

namespace Foundgine.E2E.Tests;

/// <summary>
/// Opt-in: set FOUNDGINE_POSTGRES_CONNECTION_STRING. Proves the marker, rollback and fencing behaviour of
/// <see cref="PostgresMutationMarkerProvider"/> against a real PostgreSQL. Creates the tables
/// <c>fg_marker_test</c> and <c>FgMarkerItem</c> and uses new ids on every run.
/// </summary>
public sealed class PostgresMutationMarkerProviderE2ETests
{
    private const string MarkerTable = "fg_marker_test";

    private static readonly EntityId Item = new(1);

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("FOUNDGINE_POSTGRES_CONNECTION_STRING")!;

    [PostgreSqlFact]
    public async Task Committed_execution_reconciles_as_applied()
    {
        var provider = await Provider(TimeSpan.FromSeconds(5));
        var id = NewId();

        Assert.Equal("done", provider.ExecuteInMarkedTransaction(id, (_, _) => "done"));

        var verdict = await provider.ReconcileAsync(Query(id));
        Assert.Equal(MutationVerdictOutcome.Applied, verdict.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(verdict.EvidenceFingerprint));
    }

    [PostgreSqlFact]
    public async Task Rolled_back_failure_is_not_applied_and_leaves_no_marker()
    {
        var provider = await Provider(TimeSpan.FromSeconds(5));
        var id = NewId();

        Assert.Throws<MutationNotAppliedException>(() =>
            provider.ExecuteInMarkedTransaction<int>(id, (_, _) => throw new InvalidOperationException("boom")));

        Assert.Equal(0L, await Scalar($"SELECT count(*) FROM {MarkerTable} WHERE request_id = '{id}'"));
        Assert.Equal(MutationVerdictOutcome.NotApplied, (await provider.ReconcileAsync(Query(id))).Outcome);
    }

    [PostgreSqlFact]
    public async Task Fence_stops_a_late_execution()
    {
        var provider = await Provider(TimeSpan.FromSeconds(5));
        var id = NewId();
        Assert.Equal(MutationVerdictOutcome.NotApplied, (await provider.ReconcileAsync(Query(id))).Outcome);
        var ran = false;

        Assert.Throws<MutationNotAppliedException>(() => provider.ExecuteInMarkedTransaction(id, (_, _) => ran = true));

        Assert.False(ran);
        Assert.Equal(MutationVerdictOutcome.NotApplied, (await provider.ReconcileAsync(Query(id))).Outcome);
    }

    [PostgreSqlFact]
    public async Task Reconcile_cannot_decide_while_the_execution_is_open_then_waits_for_its_commit()
    {
        var shortWait = await Provider(TimeSpan.FromMilliseconds(300));
        var longWait = await Provider(TimeSpan.FromSeconds(20));
        var id = NewId();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var execution = Task.Run(() => longWait.ExecuteInMarkedTransaction(id, (_, _) =>
        {
            started.Set();
            return release.Wait(TimeSpan.FromSeconds(15));
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the execution did not start");

        Assert.Equal(MutationVerdictOutcome.Unknown, (await shortWait.ReconcileAsync(Query(id))).Outcome);

        var waiting = Task.Run(async () => await longWait.ReconcileAsync(Query(id)));
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted, "reconcile must wait for the open execution");
        release.Set();

        Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(MutationVerdictOutcome.Applied, (await waiting.WaitAsync(TimeSpan.FromSeconds(20))).Outcome);
    }

    [PostgreSqlFact]
    public async Task A_swallowed_statement_error_is_not_reported_as_committed()
    {
        var provider = await Provider(TimeSpan.FromSeconds(5));
        var id = NewId();

        Assert.Throws<MutationNotAppliedException>(() => provider.ExecuteInMarkedTransaction(id,
            (connection, transaction) =>
            {
                try
                {
                    Run(connection, transaction, "SELECT 1 / 0");
                }
                catch (DbException)
                {
                    // the transaction can now only roll back
                }

                return 0;
            }));

        Assert.Equal(0L, await Scalar($"SELECT count(*) FROM {MarkerTable} WHERE request_id = '{id}'"));
    }

    [PostgreSqlFact]
    public async Task A_unique_violation_inside_the_mutation_is_not_described_as_a_fence()
    {
        var provider = await Provider(TimeSpan.FromSeconds(5));
        var id = NewId();

        var failure = Assert.Throws<MutationNotAppliedException>(() => provider.ExecuteInMarkedTransaction(id,
            (connection, transaction) =>
            {
                Run(connection, transaction,
                    $"INSERT INTO {MarkerTable} (request_id, state) VALUES ('{id}', 'applied')");
                return 0;
            }));

        Assert.DoesNotContain("already taken", failure.Message, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task Default_delegate_commits_the_row_and_marker_together_and_respects_a_fence()
    {
        var provider = await Provider(TimeSpan.FromSeconds(5));
        var row = Random.Shared.NextInt64(1, long.MaxValue);
        await Scalar($"INSERT INTO \"FgMarkerItem\" (\"Id\", \"Name\") VALUES ({row}, 'before')");

        var applied = NewId();
        var result = provider.ExecuteBatch(Update(row, "after"), Context(applied));

        Assert.Equal(1, result.TotalAffectedRows);
        Assert.Equal("after", await Scalar($"SELECT \"Name\" FROM \"FgMarkerItem\" WHERE \"Id\" = {row}"));
        Assert.Equal(MutationVerdictOutcome.Applied, (await provider.ReconcileAsync(Query(applied))).Outcome);

        var fenced = NewId();
        await provider.ReconcileAsync(Query(fenced));

        Assert.Throws<MutationNotAppliedException>(() => provider.ExecuteBatch(Update(row, "late"), Context(fenced)));
        Assert.Equal("after", await Scalar($"SELECT \"Name\" FROM \"FgMarkerItem\" WHERE \"Id\" = {row}"));
    }

    [Theory]
    [InlineData("t; DROP TABLE x", 1000)]
    [InlineData("marker\n", 1000)]
    [InlineData("marker", 0)]
    [InlineData("marker", 700_000)]
    public void Unsafe_table_names_and_timeouts_are_rejected(string table, int timeoutMilliseconds)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PostgresMutationMarkerProvider(
            () => throw new InvalidOperationException(), Metadata(), table,
            TimeSpan.FromMilliseconds(timeoutMilliseconds)));
    }

    private static async Task<PostgresMutationMarkerProvider> Provider(TimeSpan lockTimeout)
    {
        var provider = new PostgresMutationMarkerProvider(
            () => new NpgsqlConnection(ConnectionString), Metadata(), MarkerTable, lockTimeout);
        await provider.EnsureSchemaAsync();
        await Scalar("CREATE TABLE IF NOT EXISTS \"FgMarkerItem\" (\"Id\" bigint PRIMARY KEY, \"Name\" text NOT NULL)");
        return provider;
    }

    private static async Task<object?> Scalar(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void Run(DbConnection connection, DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static MutationReconciliationQuery Query(string id) =>
        new(id, "t", "s", "k", "plan", "corr", DateTimeOffset.UtcNow);

    private static ExecutionContext Context(string requestId) =>
        new(new Dictionary<string, object?> { [ExecutionContextKeys.ExecutionRequestId] = requestId });

    private static MetadataRegistry Metadata()
    {
        var registry = new MetadataRegistry();
        registry.Register(new EntityMetadata(
            Item,
            "FgMarkerItem",
            [new ColumnMetadata(new ColumnId(1), "Id"), new ColumnMetadata(new ColumnId(2), "Name")],
            Fields:
            [
                new FieldMetadata(new FieldId(1), "Id", typeof(long), new ColumnReference(Item, new ColumnId(1))),
                new FieldMetadata(new FieldId(2), "Name", typeof(string), new ColumnReference(Item, new ColumnId(2)))
            ],
            PrimaryKey: new ColumnReference(Item, new ColumnId(1))));
        return registry;
    }

    private static ExecutionMutationIR Update(long row, string name)
    {
        var columns = new Dictionary<FieldId, ColumnId?>
        {
            [new FieldId(1)] = new ColumnId(1),
            [new FieldId(2)] = new ColumnId(2)
        };
        var entity = new MutationEntitySchema(
            Item, "FgMarkerItem", new HashSet<ColumnId> { new(1), new(2) }, columns, new ColumnId(1));
        return ExecutionMutationIR.From(new MutationBatchPlan(
        [
            new MutationOperation(entity, MutationKind.Update, [new MutationFieldValue(new ColumnId(2), name)],
                new SemanticFieldFilter(new FieldId(1), SemanticFilterOperator.Eq, row), null,
                [new FieldId(1), new FieldId(2)])
        ], []));
    }
}
