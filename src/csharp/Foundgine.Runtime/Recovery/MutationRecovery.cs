using System.Security.Cryptography;
using System.Text;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Runtime.ControlPlane.AuditLog;

namespace Foundgine.Runtime.Recovery;

/// <summary>
/// Resolves mutation executions whose outcome the engine could not settle: a crash or lost response (<c>Started</c>
/// past its lease), a provider failure after dispatch (<c>InDoubt</c>), or a committed mutation whose success audit
/// event was never acknowledged. Resolution is driven by the provider's <see cref="IMutationReconciler"/>; an operator
/// can resolve an <c>InDoubt</c> record by hand with <see cref="ResolveAsync"/>. Success audit repair is at-least-once:
/// if the original audit write actually persisted but was not acknowledged, a second event is appended. Port of the
/// Java <c>MutationRecovery</c>.
/// </summary>
public sealed class MutationRecovery
{
    private const int MaxCasAttempts = 3;
    private const string AuditTool = "semantic_mutation";
    private const string UnknownActor = "anonymous";

    private readonly IMutationExecutionLedger _ledger;
    private readonly IMutationReconciler? _reconciler;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _time;

    public MutationRecovery(IMutationExecutionLedger ledger, IMutationReconciler? reconciler, IAuditLog auditLog, TimeProvider? timeProvider = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _reconciler = reconciler;
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<MutationReconciliationReport> ReconcileAsync(
        string? tenant, string? subject, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var changed = false;
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
        {
            var rec = await _ledger.FindAsync(tenant, subject, idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (rec is null)
                return new(MutationReconciliationOutcome.NotFound, null, changed, "No execution is recorded for this idempotency key.");
            var now = _time.GetUtcNow();

            if (rec.State == MutationExecutionState.Aborted)
                return Report(MutationReconciliationOutcome.Aborted, rec, changed, "Not applied; the key may be retried.");
            if (rec.State == MutationExecutionState.Committed)
                return await RepairAuditAsync(rec, changed, cancellationToken).ConfigureAwait(false);

            if (rec.State == MutationExecutionState.Started)
            {
                if (rec.LeaseExpiresAt is { } lease && now < lease)
                    return Report(MutationReconciliationOutcome.InProgress, rec, changed, "Execution is still within its lease.");
                var doubt = await _ledger.TryUpdateAsync(rec.Version, rec.Transition(
                    MutationExecutionState.InDoubt, now, rec.Receipt, rec.AffectedRows, "lease expired without a recorded outcome"),
                    cancellationToken).ConfigureAwait(false);
                if (doubt is null) continue;
                changed = true;
                rec = doubt; // continue with the InDoubt record
            }

            var verdict = await AskAsync(rec, cancellationToken).ConfigureAwait(false);
            if (verdict is null || verdict.Outcome == MutationVerdictOutcome.Unknown)
                return Report(MutationReconciliationOutcome.Unresolved, rec, changed, _reconciler is null
                    ? "No reconciler is configured; resolve the execution manually."
                    : "The provider could not tell whether the mutation was applied.");

            var applied = verdict.Outcome == MutationVerdictOutcome.Applied;
            var next = applied
                ? rec.Transition(MutationExecutionState.Committed, now,
                    rec.Receipt.WithOutcome("reconciled", verdict.EvidenceFingerprint!, now),
                    rec.AffectedRows, "reconciled: provider confirmed the mutation")
                : rec.Transition(MutationExecutionState.Aborted, now,
                    rec.Receipt.WithOutcome("aborted", "aborted", now), [],
                    "reconciled: provider confirmed the mutation was not applied");
            var stored = await _ledger.TryUpdateAsync(rec.Version, next, cancellationToken).ConfigureAwait(false);
            if (stored is null) continue; // another writer moved first; re-read
            if (applied) return await RepairAuditAsync(stored, true, cancellationToken).ConfigureAwait(false);
            return Report(MutationReconciliationOutcome.Aborted, stored, true,
                "Provider confirmed the mutation was not applied; the key may be retried.");
        }

        var latest = await _ledger.FindAsync(tenant, subject, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return Report(MutationReconciliationOutcome.Unresolved, latest, changed, "The record kept changing; retry reconciliation.");
    }

    /// <summary>Reconciles up to <paramref name="limit"/> unresolved executions, oldest first.</summary>
    public async ValueTask<IReadOnlyList<MutationReconciliationReport>> SweepAsync(int limit, CancellationToken cancellationToken = default)
    {
        var reports = new List<MutationReconciliationReport>();
        foreach (var rec in await _ledger.ListUnresolvedAsync(limit, cancellationToken).ConfigureAwait(false))
            reports.Add(await ReconcileAsync(rec.Tenant, rec.Subject, rec.IdempotencyKey, cancellationToken).ConfigureAwait(false));
        return reports;
    }

    /// <summary>
    /// Operator override for an <c>InDoubt</c> execution after checking the target system by hand.
    /// <paramref name="applied"/> true records Committed, false records Aborted (which lets the key be retried: only
    /// choose it when the mutation is verifiably absent and cannot still commit).
    /// </summary>
    public async ValueTask<MutationReconciliationReport> ResolveAsync(
        string? tenant, string? subject, string idempotencyKey, bool applied, string operatorId, string? note,
        CancellationToken cancellationToken = default)
    {
        if (!MutationExecutionRecord.IsSafeId(operatorId))
            throw new ArgumentException("operatorId must contain 1-128 safe identifier characters", nameof(operatorId));
        var rec = await _ledger.FindAsync(tenant, subject, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (rec is null)
            return new(MutationReconciliationOutcome.NotFound, null, false, "No execution is recorded for this idempotency key.");
        if (rec.State != MutationExecutionState.InDoubt)
            throw new InvalidOperationException($"Only an InDoubt execution can be resolved manually (state is {rec.State}).");

        var text = "operator " + operatorId + (string.IsNullOrWhiteSpace(note) ? "" : ": " + Sanitize(note));
        var now = _time.GetUtcNow();
        var manual = Sha("manual:" + rec.RequestId);
        var next = applied
            ? rec.Transition(MutationExecutionState.Committed, now, rec.Receipt.WithOutcome("reconciled", manual, now), rec.AffectedRows, text)
            : rec.Transition(MutationExecutionState.Aborted, now, rec.Receipt.WithOutcome("aborted", "aborted", now), [], text);
        var stored = await _ledger.TryUpdateAsync(rec.Version, next, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The execution changed while it was being resolved; re-read and retry.");
        if (applied) return await RepairAuditAsync(stored, true, cancellationToken).ConfigureAwait(false);

        // "Not applied" re-opens the key, so the override itself must leave an audit trail. The ledger change is already
        // durable; a failing audit sink is reported, not hidden.
        var detail = "Resolved as not applied by an operator.";
        try
        {
            _auditLog.Record(AuditEvent.CreateCorrelated(AuditCategory.MutationFailed, AuditTool, operatorId, rec.Tenant, manual,
                "Operator resolved execution as not applied; receipt=" + rec.RequestId, rec.CorrelationId));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            detail += " The audit event for this decision was NOT persisted.";
        }
        return Report(MutationReconciliationOutcome.Aborted, stored, true, detail);
    }

    /// <summary>The provider's verdict, or null when there is no reconciler or it failed.</summary>
    private async ValueTask<MutationReconciliationVerdict?> AskAsync(MutationExecutionRecord rec, CancellationToken cancellationToken)
    {
        if (_reconciler is null) return null;
        try
        {
            return await _reconciler.ReconcileAsync(new MutationReconciliationQuery(rec.RequestId, rec.Tenant, rec.Subject,
                rec.IdempotencyKey, rec.PlanFingerprint, rec.CorrelationId, rec.StartedAt), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>For a Committed record whose success audit was not acknowledged, appends the event and records the ack.</summary>
    private async ValueTask<MutationReconciliationReport> RepairAuditAsync(MutationExecutionRecord rec, bool changed, CancellationToken cancellationToken)
    {
        if (rec.AuditAcknowledged)
            return Report(MutationReconciliationOutcome.Committed, rec, changed, "The mutation is committed.");
        try
        {
            var receipt = rec.Receipt;
            _auditLog.Record(AuditEvent.CreateCorrelated(AuditCategory.MutationSucceeded, AuditTool, rec.Subject ?? UnknownActor,
                rec.Tenant, receipt.ResultFingerprint, "Mutation completed; receipt=" + receipt.RequestId + "; recovered", rec.CorrelationId));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Report(MutationReconciliationOutcome.Committed, rec, changed,
                "The mutation is committed; its success audit event is still unacknowledged.");
        }
        var acked = await _ledger.TryUpdateAsync(rec.Version, rec.WithAuditAcknowledged(_time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return Report(MutationReconciliationOutcome.Committed, acked ?? rec, true, acked is not null
            ? "The mutation is committed; its audit event was recovered."
            : "The mutation is committed; the audit event was appended but not acknowledged.");
    }

    private static MutationReconciliationReport Report(MutationReconciliationOutcome o, MutationExecutionRecord? r, bool changed, string detail)
        => new(o, r, changed, detail);

    // One '?' per code point (as Java's regex does), so a supplementary character is not doubled.
    private static string Sanitize(string note)
    {
        var sb = new StringBuilder(note.Length);
        foreach (var rune in note.EnumerateRunes())
        {
            var v = rune.Value;
            var ok = v is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                or ' ' or '.' or ',' or ':' or ';' or '_' or '(' or ')' or '/' or '-';
            sb.Append(ok ? (char)v : '?');
        }
        return sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
