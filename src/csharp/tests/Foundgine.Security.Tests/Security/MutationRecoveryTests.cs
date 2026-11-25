using Foundgine.Core.Execution;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Runtime.ControlPlane.AuditLog;
using Foundgine.Runtime.Recovery;
using Xunit;

namespace Foundgine.Security.Tests.Security;

/// <summary>Port of the Java recovery tests: reconciliation of Started / InDoubt / unacknowledged-audit executions.</summary>
public sealed class MutationRecoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeReconciler(Func<MutationReconciliationQuery, MutationReconciliationVerdict> answer) : IMutationReconciler
    {
        public List<MutationReconciliationQuery> Queries { get; } = [];

        public ValueTask<MutationReconciliationVerdict> ReconcileAsync(MutationReconciliationQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return ValueTask.FromResult(answer(query));
        }
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public void Record(AuditEvent auditEvent) => throw new InvalidOperationException("audit sink down");
        public IReadOnlyList<AuditEvent> Query(string? toolName = null, string? tenant = null) => [];
    }

    /// <summary>Fails the first <c>failures</c> updates (as if another writer moved first), then delegates.</summary>
    private sealed class FlakyLedger(IMutationExecutionLedger inner, int failures) : IMutationExecutionLedger
    {
        private int _failures = failures;
        public ValueTask<MutationLedgerCreateResult> TryCreateAsync(MutationExecutionRecord record, CancellationToken ct = default) => inner.TryCreateAsync(record, ct);
        public ValueTask<MutationExecutionRecord?> TryUpdateAsync(long expectedVersion, MutationExecutionRecord updated, CancellationToken ct = default)
            => _failures-- > 0 ? ValueTask.FromResult<MutationExecutionRecord?>(null) : inner.TryUpdateAsync(expectedVersion, updated, ct);
        public ValueTask<MutationExecutionRecord?> FindAsync(string? tenant, string? subject, string key, CancellationToken ct = default) => inner.FindAsync(tenant, subject, key, ct);
        public ValueTask<MutationExecutionRecord?> FindByRequestIdAsync(string requestId, CancellationToken ct = default) => inner.FindByRequestIdAsync(requestId, ct);
        public ValueTask<IReadOnlyList<MutationExecutionRecord>> ListUnresolvedAsync(int limit, CancellationToken ct = default) => inner.ListUnresolvedAsync(limit, ct);
    }

    private static MutationExecutionRecord Started(string key, DateTimeOffset started, string requestId = "r-1")
    {
        var receipt = new ExecutionReceipt(requestId, "pending", "sm-1", 1, 1, 1, 1, "intent", "plan", "auth", "provider",
            started, started, [1], ["write"], "pending", CorrelationId: "corr-1");
        return new MutationExecutionRecord("t", "s", key, requestId, "plan", "corr-1", MutationExecutionState.Started, 1,
            0, started, started, started.AddSeconds(60), receipt, [], false, null);
    }

    private static async Task<(InMemoryMutationExecutionLedger Ledger, MutationExecutionRecord Rec)> Seed(string key = "k1", DateTimeOffset? at = null)
    {
        var ledger = new InMemoryMutationExecutionLedger();
        return (ledger, (await ledger.TryCreateAsync(Started(key, at ?? T0))).Record);
    }

    private static MutationRecovery Recovery(IMutationExecutionLedger l, IMutationReconciler? r, IAuditLog a, FakeTime t)
        => new(l, r, a, t);

    [Fact]
    public async Task UnknownKeyIsNotFound()
    {
        var rec = Recovery(new InMemoryMutationExecutionLedger(), null, new InMemoryAuditLog(), new FakeTime(T0));
        var report = await rec.ReconcileAsync("t", "s", "nope");
        Assert.Equal(MutationReconciliationOutcome.NotFound, report.Outcome);
        Assert.Null(report.Record);
        Assert.False(report.Changed);
    }

    [Fact]
    public async Task StartedWithinLeaseIsLeftAlone()
    {
        var (ledger, seeded) = await Seed();
        var reconciler = new FakeReconciler(_ => MutationReconciliationVerdict.Applied("x"));
        var report = await Recovery(ledger, reconciler, new InMemoryAuditLog(), new FakeTime(T0.AddSeconds(30))).ReconcileAsync("t", "s", "k1");
        Assert.Equal(MutationReconciliationOutcome.InProgress, report.Outcome);
        Assert.False(report.Changed);
        Assert.Empty(reconciler.Queries);
        Assert.Equal(seeded.Version, (await ledger.FindAsync("t", "s", "k1"))!.Version);
    }

    [Fact]
    public async Task ExpiredLeaseWithoutReconcilerBecomesInDoubtAndStaysUnresolved()
    {
        var (ledger, _) = await Seed();
        var report = await Recovery(ledger, null, new InMemoryAuditLog(), new FakeTime(T0.AddSeconds(61))).ReconcileAsync("t", "s", "k1");
        Assert.Equal(MutationReconciliationOutcome.Unresolved, report.Outcome);
        Assert.True(report.Changed);
        Assert.Equal(MutationExecutionState.InDoubt, report.Record!.State);
        Assert.Equal("lease expired without a recorded outcome", report.Record.Resolution);
        Assert.Contains("No reconciler", report.Detail);
    }

    [Fact]
    public async Task AppliedVerdictCommitsAndRepairsTheAuditEvent()
    {
        var (ledger, seeded) = await Seed();
        var audit = new InMemoryAuditLog();
        var reconciler = new FakeReconciler(_ => MutationReconciliationVerdict.Applied("evidence-fp"));
        var now = T0.AddSeconds(61);
        var report = await Recovery(ledger, reconciler, audit, new FakeTime(now)).ReconcileAsync("t", "s", "k1");

        Assert.Equal(MutationReconciliationOutcome.Committed, report.Outcome);
        Assert.True(report.Changed);
        var stored = report.Record!;
        Assert.Equal(MutationExecutionState.Committed, stored.State);
        Assert.True(stored.AuditAcknowledged);
        Assert.Equal("reconciled", stored.Receipt.Status);
        Assert.Equal("evidence-fp", stored.Receipt.ResultFingerprint);
        Assert.Equal(now, stored.Receipt.CompletedAt);
        Assert.Equal(seeded.Version + 3, stored.Version); // InDoubt, Committed, audit acknowledged
        // the query handed to the provider carries identifiers only
        var q = reconciler.Queries[0];
        Assert.Equal("r-1", q.RequestId);
        Assert.Equal("plan", q.PlanFingerprint);
        Assert.Equal("corr-1", q.CorrelationId);
        // one success event, correlated
        var ev = Assert.Single(audit.Query("semantic_mutation"));
        Assert.Equal(AuditCategory.MutationSucceeded, ev.Category);
        Assert.Equal("corr-1", ev.CorrelationId);
        Assert.Equal("evidence-fp", ev.Fingerprint);
        Assert.Equal("s", ev.Actor);
    }

    [Fact]
    public async Task NotAppliedVerdictAbortsAndClearsAffectedRows()
    {
        var (ledger, _) = await Seed();
        var reconciler = new FakeReconciler(_ => MutationReconciliationVerdict.NotApplied("tombstoned"));
        var report = await Recovery(ledger, reconciler, new InMemoryAuditLog(), new FakeTime(T0.AddSeconds(61))).ReconcileAsync("t", "s", "k1");
        Assert.Equal(MutationReconciliationOutcome.Aborted, report.Outcome);
        Assert.Equal(MutationExecutionState.Aborted, report.Record!.State);
        Assert.Equal("aborted", report.Record.Receipt.Status);
        Assert.Empty(report.Record.AffectedRows);
        // an aborted execution stays aborted on the next pass
        var again = await Recovery(ledger, reconciler, new InMemoryAuditLog(), new FakeTime(T0.AddMinutes(5))).ReconcileAsync("t", "s", "k1");
        Assert.Equal(MutationReconciliationOutcome.Aborted, again.Outcome);
        Assert.False(again.Changed);
    }

    [Fact]
    public async Task UnknownVerdictAndReconcilerFailureBothLeaveItUnresolved()
    {
        foreach (var answer in new Func<MutationReconciliationQuery, MutationReconciliationVerdict>[]
                 { _ => MutationReconciliationVerdict.Unknown("cannot tell"), _ => throw new IOException("provider down") })
        {
            var (ledger, _) = await Seed();
            var report = await Recovery(ledger, new FakeReconciler(answer), new InMemoryAuditLog(), new FakeTime(T0.AddSeconds(61))).ReconcileAsync("t", "s", "k1");
            Assert.Equal(MutationReconciliationOutcome.Unresolved, report.Outcome);
            Assert.Equal(MutationExecutionState.InDoubt, report.Record!.State);
            Assert.Contains("could not tell", report.Detail);
        }
    }

    [Fact]
    public async Task CommittedWithFailingAuditSinkStaysUnacknowledgedAndIsRepairedLater()
    {
        var (ledger, seeded) = await Seed();
        var committed = seeded.Transition(MutationExecutionState.Committed, T0.AddSeconds(1), seeded.Receipt.WithOutcome("succeeded", "result-fp", T0.AddSeconds(1)), [1], "done");
        Assert.NotNull(await ledger.TryUpdateAsync(seeded.Version, committed));

        var failed = await Recovery(ledger, null, new ThrowingAuditLog(), new FakeTime(T0.AddSeconds(5))).ReconcileAsync("t", "s", "k1");
        Assert.Equal(MutationReconciliationOutcome.Committed, failed.Outcome);
        Assert.False(failed.Record!.AuditAcknowledged);
        Assert.False(failed.Changed);
        Assert.Single(await ledger.ListUnresolvedAsync(10));

        var audit = new InMemoryAuditLog();
        var repaired = await Recovery(ledger, null, audit, new FakeTime(T0.AddSeconds(6))).ReconcileAsync("t", "s", "k1");
        Assert.True(repaired.Record!.AuditAcknowledged);
        Assert.True(repaired.Changed);
        Assert.Equal("result-fp", audit.Query()[0].Fingerprint);
        Assert.Empty((await ledger.ListUnresolvedAsync(10)));
        // fully settled: no second event
        await Recovery(ledger, null, audit, new FakeTime(T0.AddSeconds(7))).ReconcileAsync("t", "s", "k1");
        Assert.Single(audit.Query());
    }

    [Fact]
    public async Task LosingACompareAndSetRaceIsRetriedAndExhaustionIsReported()
    {
        var (inner, _) = await Seed();
        var time = new FakeTime(T0.AddSeconds(61));
        var applied = new FakeReconciler(_ => MutationReconciliationVerdict.Applied("fp"));
        var ok = await Recovery(new FlakyLedger(inner, 2), applied, new InMemoryAuditLog(), time).ReconcileAsync("t", "s", "k1");
        Assert.Equal(MutationReconciliationOutcome.Committed, ok.Outcome); // third attempt wins

        var (inner2, _) = await Seed("k2");
        var stuck = await Recovery(new FlakyLedger(inner2, int.MaxValue), applied, new InMemoryAuditLog(), time).ReconcileAsync("t", "s", "k2");
        Assert.Equal(MutationReconciliationOutcome.Unresolved, stuck.Outcome);
        Assert.Contains("kept changing", stuck.Detail);
        Assert.Equal(MutationExecutionState.Started, stuck.Record!.State);
    }

    [Fact]
    public async Task SweepReconcilesOldestFirstAndSkipsSettledRecords()
    {
        var ledger = new InMemoryMutationExecutionLedger();
        await ledger.TryCreateAsync(Started("late", T0.AddSeconds(10), "r-late"));
        await ledger.TryCreateAsync(Started("early", T0, "r-early"));
        var settled = (await ledger.TryCreateAsync(Started("done", T0.AddSeconds(5), "r-done"))).Record;
        await ledger.TryUpdateAsync(settled.Version, settled.Transition(MutationExecutionState.Aborted, T0, settled.Receipt, [], "x"));

        var reports = await Recovery(ledger, new FakeReconciler(_ => MutationReconciliationVerdict.NotApplied()), new InMemoryAuditLog(),
            new FakeTime(T0.AddMinutes(5))).SweepAsync(10);
        Assert.Equal(new[] { "early", "late" }, reports.Select(r => r.Record!.IdempotencyKey).ToArray());
        Assert.True(reports.All(r => r.Outcome == MutationReconciliationOutcome.Aborted));
    }

    private static async Task<(InMemoryMutationExecutionLedger Ledger, MutationRecovery Recovery, InMemoryAuditLog Audit)> InDoubt()
    {
        var (ledger, _) = await Seed();
        var audit = new InMemoryAuditLog();
        await Recovery(ledger, null, audit, new FakeTime(T0.AddSeconds(61))).ReconcileAsync("t", "s", "k1"); // -> InDoubt
        return (ledger, Recovery(ledger, null, audit, new FakeTime(T0.AddSeconds(120))), audit);
    }

    [Fact]
    public async Task OperatorCanCommitAnInDoubtExecutionAndTheNoteIsSanitized()
    {
        var (_, recovery, audit) = await InDoubt();
        var report = await recovery.ResolveAsync("t", "s", "k1", applied: true, "ops-1", "checked <db> \U0001F600 ok");
        Assert.Equal(MutationReconciliationOutcome.Committed, report.Outcome);
        Assert.Equal("operator ops-1: checked ?db? ? ok", report.Record!.Resolution); // one '?' per code point
        Assert.True(report.Record.AuditAcknowledged);
        Assert.Single(audit.Query(), e => e.Category == AuditCategory.MutationSucceeded);
    }

    [Fact]
    public async Task OperatorAbortReopensTheKeyAndLeavesItsOwnAuditTrail()
    {
        var (_, recovery, audit) = await InDoubt();
        var report = await recovery.ResolveAsync("t", "s", "k1", applied: false, "ops-1", new string('x', 500));
        Assert.Equal(MutationReconciliationOutcome.Aborted, report.Outcome);
        Assert.Equal(("operator ops-1: ").Length + 200, report.Record!.Resolution!.Length);
        var e = Assert.Single(audit.Query());
        Assert.Equal(AuditCategory.MutationFailed, e.Category);
        Assert.Equal("ops-1", e.Actor);
        Assert.Equal("corr-1", e.CorrelationId);
    }

    [Fact]
    public async Task OperatorAbortReportsAnAuditFailureInsteadOfHidingIt()
    {
        var (ledger, _) = await Seed();
        await Recovery(ledger, null, new InMemoryAuditLog(), new FakeTime(T0.AddSeconds(61))).ReconcileAsync("t", "s", "k1");
        var report = await Recovery(ledger, null, new ThrowingAuditLog(), new FakeTime(T0.AddSeconds(90)))
            .ResolveAsync("t", "s", "k1", applied: false, "ops-1", null);
        Assert.Equal(MutationExecutionState.Aborted, (await ledger.FindAsync("t", "s", "k1"))!.State);
        Assert.Contains("NOT persisted", report.Detail);
    }

    [Fact]
    public async Task ManualResolutionIsRestrictedToInDoubtAndSafeOperatorIds()
    {
        var (ledger, _) = await Seed(); // still Started
        var recovery = Recovery(ledger, null, new InMemoryAuditLog(), new FakeTime(T0));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recovery.ResolveAsync("t", "s", "k1", true, "ops-1", null));
        await Assert.ThrowsAsync<ArgumentException>(async () => await recovery.ResolveAsync("t", "s", "k1", true, "bad id", null));
        await Assert.ThrowsAsync<ArgumentException>(async () => await recovery.ResolveAsync("t", "s", "k1", true, "ops\n", null));
        var missing = await recovery.ResolveAsync("t", "s", "none", true, "ops-1", null);
        Assert.Equal(MutationReconciliationOutcome.NotFound, missing.Outcome);
    }

    [Fact]
    public void SafeIdsRejectATrailingNewlineLikeJavasFullMatch()
    {
        Assert.True(MutationExecutionRecord.IsSafeId("k-1.a:b_c"));
        Assert.False(MutationExecutionRecord.IsSafeId("k\n"));
        Assert.False(MutationExecutionRecord.IsSafeId(""));
        Assert.False(MutationExecutionRecord.IsSafeId(new string('a', 129)));
        Assert.Throws<ArgumentException>(() => Started("k\n", T0));
    }

    [Fact]
    public void AppliedVerdictNeedsEvidenceAndReceiptsCanBeReconciled()
    {
        Assert.Throws<ArgumentException>(() => MutationReconciliationVerdict.Applied(" "));
        Assert.Equal(MutationVerdictOutcome.Unknown, MutationReconciliationVerdict.Unknown().Outcome);
        var r = Started("k", T0).Receipt;
        var w = r.WithOutcome("reconciled", "fp", T0.AddSeconds(9));
        Assert.Equal("reconciled", w.Status);
        Assert.Equal("fp", w.ResultFingerprint);
        Assert.Equal(T0.AddSeconds(9), w.CompletedAt);
        Assert.Equal(r.PlanFingerprint, w.PlanFingerprint);
        Assert.Equal(r.CorrelationId, w.CorrelationId);
    }
}
