using Foundgine.Core.Abstractions;
using Foundgine.Core.Execution;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Core.Semantic;
using Foundgine.Core.Semantic.Authorization;
using Foundgine.Core.Semantic.Metadata;
using Foundgine.Core.Semantic.Mutation;
using Foundgine.Core.Semantic.Query;
using Foundgine.Runtime;
using Foundgine.Runtime.ControlPlane.AuditLog;
using Foundgine.Runtime.Recovery;
using Xunit;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;

namespace Foundgine.Security.Tests.Security;

/// <summary>
/// Engine-level tests of the write-ahead execution ledger: replay of a committed key, abort and retry, in-doubt
/// refusal, in-progress leases, audit and ledger persistence failures. Counterpart of Java's
/// <c>MutationRecoveryEngineTest</c>.
/// </summary>
public sealed class MutationEngineLedgerTests
{
    private static readonly EntityId Customer = new(1);

    private static readonly SemanticModel Model = new SemanticModelBuilder()
        .Entity(Customer, "Customer", c => c
            .Identity(new FieldId(1), "Id")
            .Field(new FieldId(2), "Name", typeof(string)))
        .Build();

    private static SemanticMutationRequest Request(string value) => new(new SemanticMutationOperationGraph([
        SemanticMutationBuilder.Update(
            Customer,
            [new SemanticMutationField(new FieldId(2), value)],
            new SemanticFieldFilter(new FieldId(1), SemanticFilterOperator.Eq, 1))]));

    private static ExecutionContext Context(string? key = null, string? requestId = null)
    {
        var values = new Dictionary<string, object?>();
        if (key is not null) values[ExecutionContextKeys.IdempotencyKey] = key;
        if (requestId is not null) values[ExecutionContextKeys.ExecutionRequestId] = requestId;
        return new ExecutionContext(values);
    }

    private static FoundgineMutationEngine Engine(
        FakeProvider provider, IMutationExecutionLedger? ledger, IAuditLog? audit = null)
    {
        var columns = new Dictionary<FieldId, ColumnId?>
        {
            [new FieldId(1)] = new ColumnId(11),
            [new FieldId(2)] = new ColumnId(12)
        };
        var entity = new MutationEntitySchema(
            Customer, "Customer", columns.Values.Select(x => x!.Value).ToHashSet(), columns, new ColumnId(11));
        return new FoundgineMutationEngine(
            new FakeSchema(entity), new AllowAllSemanticAuthorizationPolicy(), provider, Model,
            auditLog: audit, ledger: ledger);
    }

    [Fact]
    public async Task Committed_key_replays_the_original_result_without_calling_the_provider()
    {
        var provider = new FakeProvider();
        var ledger = new InMemoryMutationExecutionLedger();
        var engine = Engine(provider, ledger);

        var first = await engine.ExecuteAsync(Request("a"), Context("k1"));
        var second = await engine.ExecuteAsync(Request("a"), Context("k1"));

        Assert.Equal(1, provider.Calls);
        Assert.NotNull(first.Receipt);
        Assert.NotNull(second.Receipt);
        Assert.Equal(first.Receipt!.RequestId, second.Receipt!.RequestId);
        Assert.Equal(first.ResultFingerprint, second.ResultFingerprint);
        Assert.Equal(1, second.Result.TotalAffectedRows);
        var record = await ledger.FindAsync(null, null, "k1");
        Assert.Equal(MutationExecutionState.Committed, record!.State);
        Assert.True(record.AuditAcknowledged);
        Assert.Equal(first.Receipt.RequestId, provider.LastRequestId);
    }

    [Fact]
    public async Task Same_key_with_a_different_plan_is_refused()
    {
        var provider = new FakeProvider();
        var engine = Engine(provider, new InMemoryMutationExecutionLedger());
        await engine.ExecuteAsync(Request("a"), Context("k1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(Request("b"), Context("k1")));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Without_a_ledger_the_key_is_ignored_and_a_caller_request_id_is_removed()
    {
        var provider = new FakeProvider();
        var engine = Engine(provider, null);

        await engine.ExecuteAsync(Request("a"), Context("k1", "spoofed"));
        Assert.Null(provider.LastRequestId);
        await engine.ExecuteAsync(Request("a"), Context("k1"));

        Assert.Equal(2, provider.Calls);
        Assert.Null(engine.Recovery);
    }

    [Fact]
    public void Ledger_engine_exposes_recovery()
    {
        Assert.NotNull(Engine(new FakeProvider(), new InMemoryMutationExecutionLedger()).Recovery);
    }

    [Fact]
    public async Task Malformed_key_is_thrown_synchronously()
    {
        var engine = Engine(new FakeProvider(), new InMemoryMutationExecutionLedger());

        await Assert.ThrowsAsync<ArgumentException>(() => engine.ExecuteAsync(Request("a"), Context("bad key!")));
    }

    [Fact]
    public async Task Not_applied_failure_aborts_and_the_key_can_be_retried()
    {
        var provider = new FakeProvider { Fail = () => new MutationNotAppliedException("rolled back") };
        var ledger = new InMemoryMutationExecutionLedger();
        var engine = Engine(provider, ledger);

        await Assert.ThrowsAsync<MutationNotAppliedException>(() => engine.ExecuteAsync(Request("a"), Context("k1")));
        Assert.Equal(MutationExecutionState.Aborted, (await ledger.FindAsync(null, null, "k1"))!.State);

        provider.Fail = null;
        var retry = await engine.ExecuteAsync(Request("a"), Context("k1"));

        Assert.NotNull(retry.Receipt);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, (await ledger.FindAsync(null, null, "k1"))!.Attempt);
    }

    [Fact]
    public async Task Other_provider_failure_leaves_the_key_in_doubt_and_blocks_retry()
    {
        var provider = new FakeProvider { Fail = () => new IOException("connection lost") };
        var ledger = new InMemoryMutationExecutionLedger();
        var engine = Engine(provider, ledger);

        var first = await Assert.ThrowsAsync<MutationInDoubtException>(() => engine.ExecuteAsync(Request("a"), Context("k1")));
        Assert.IsType<IOException>(first.InnerException);
        Assert.Equal(MutationExecutionState.InDoubt, (await ledger.FindAsync(null, null, "k1"))!.State);

        provider.Fail = null;
        await Assert.ThrowsAsync<MutationInDoubtException>(() => engine.ExecuteAsync(Request("a"), Context("k1")));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Without_a_ledger_the_original_provider_failure_surfaces()
    {
        var provider = new FakeProvider { Fail = () => new IOException("connection lost") };

        await Assert.ThrowsAsync<IOException>(() => Engine(provider, null).ExecuteAsync(Request("a"), Context()));
    }

    [Fact]
    public async Task Concurrent_execution_of_a_key_within_its_lease_is_in_progress()
    {
        var provider = new FakeProvider { Block = new ManualResetEventSlim() };
        var engine = Engine(provider, new InMemoryMutationExecutionLedger());
        var slow = Task.Run(() => engine.ExecuteAsync(Request("a"), Context("k1")));
        Assert.True(provider.Entered.Wait(TimeSpan.FromSeconds(30)), "the provider call did not start");

        await Assert.ThrowsAsync<MutationInProgressException>(() => engine.ExecuteAsync(Request("a"), Context("k1")));

        provider.Block.Set();
        await slow;
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Unacknowledged_success_audit_throws_with_the_result_attached()
    {
        var engine = Engine(new FakeProvider(), null, new SuccessFailingAuditLog());

        var failure = await Assert.ThrowsAsync<MutationAuditPersistenceException>(
            () => engine.ExecuteAsync(Request("a"), Context()));

        Assert.NotNull(failure.ExecutionResult.Receipt);
    }

    [Fact]
    public async Task Commit_the_ledger_cannot_record_throws_with_the_result_and_stays_started()
    {
        var inner = new InMemoryMutationExecutionLedger();
        var engine = Engine(new FakeProvider(), new UpdateFailingLedger(inner));

        var failure = await Assert.ThrowsAsync<MutationCommittedUnrecordedException>(
            () => engine.ExecuteAsync(Request("a"), Context("k1")));

        Assert.NotNull(failure.ExecutionResult.Receipt);
        Assert.Equal(MutationExecutionState.Started, (await inner.FindAsync(null, null, "k1"))!.State);
    }

    [Fact]
    public async Task Dry_run_approval_success_and_denial_are_audited_and_the_receipt_carries_the_approval()
    {
        var audit = new InMemoryAuditLog();
        var engine = Engine(new FakeProvider(), null, audit);

        var approval = engine.Approve(Request("a"), "human");
        var result = await engine.ExecuteApprovedAsync(approval, Context());

        var categories = audit.Query().Select(x => x.Category).ToList();
        Assert.Contains(AuditCategory.MutationDryRun, categories);
        Assert.Contains(AuditCategory.ApprovalDecided, categories);
        Assert.Contains(AuditCategory.MutationSucceeded, categories);
        Assert.Equal(approval.ApprovalId, result.Receipt!.ApprovalId);
        Assert.Equal("human", result.Receipt.ApprovedBy);

        var tampered = approval with { PlanFingerprint = "tampered" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteApprovedAsync(tampered, Context()));
        Assert.Contains(AuditCategory.MutationDenied, audit.Query().Select(x => x.Category).ToList());
    }

    private sealed class FakeSchema(MutationEntitySchema entity) : IMutationSchema
    {
        public MutationEntitySchema GetEntity(EntityId entityId) =>
            entity.Id == entityId ? entity : throw new KeyNotFoundException();

        public MutationRelationshipSchema GetRelationship(RelationshipId relationshipId) =>
            throw new KeyNotFoundException();
    }

    private sealed class FakeProvider : IMutationBatchExecutionProvider, IMutationSecurityConformanceEvaluator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? LastRequestId { get; private set; }
        public Func<Exception>? Fail { get; set; }
        public ManualResetEventSlim? Block { get; init; }
        public ManualResetEventSlim Entered { get; } = new();

        public MutationBatchResult ExecuteBatch(ExecutionMutationIR ir, ExecutionContext context)
        {
            Entered.Set();
            Block?.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref _calls);
            LastRequestId = context.EffectiveValues.TryGetValue(ExecutionContextKeys.ExecutionRequestId, out var id)
                ? id as string
                : null;
            if (Fail is { } fail) throw fail();
            return new MutationBatchResult([new MutationResult(1)]);
        }

        public MutationSecurityConformanceResult Evaluate(ExecutionMutationIR ir) =>
            new(GetType().FullName!, ir.RequiredSecurityInvariants, []);
    }

    private sealed class SuccessFailingAuditLog : IAuditLog
    {
        public void Record(AuditEvent auditEvent)
        {
            if (auditEvent.Category == AuditCategory.MutationSucceeded)
                throw new InvalidOperationException("audit sink down");
        }

        public IReadOnlyList<AuditEvent> Query(string? toolName = null, string? tenant = null) => [];
    }

    /// <summary>Creates records normally but fails every update, so a commit cannot be recorded.</summary>
    private sealed class UpdateFailingLedger(IMutationExecutionLedger inner) : IMutationExecutionLedger
    {
        public ValueTask<MutationLedgerCreateResult> TryCreateAsync(MutationExecutionRecord record, CancellationToken cancellationToken = default)
            => inner.TryCreateAsync(record, cancellationToken);

        public ValueTask<MutationExecutionRecord?> TryUpdateAsync(long expectedVersion, MutationExecutionRecord updated, CancellationToken cancellationToken = default)
            => throw new IOException("ledger down");

        public ValueTask<MutationExecutionRecord?> FindAsync(string? tenant, string? subject, string idempotencyKey, CancellationToken cancellationToken = default)
            => inner.FindAsync(tenant, subject, idempotencyKey, cancellationToken);

        public ValueTask<MutationExecutionRecord?> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken = default)
            => inner.FindByRequestIdAsync(requestId, cancellationToken);

        public ValueTask<IReadOnlyList<MutationExecutionRecord>> ListUnresolvedAsync(int limit, CancellationToken cancellationToken = default)
            => inner.ListUnresolvedAsync(limit, cancellationToken);
    }
}
