using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundgine.Core.Abstractions;
using Foundgine.Core.Execution;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Core.Semantic.Planning.Mutation;
using Foundgine.Core.Semantic;
using Foundgine.Core.Semantic.Authorization;
using Foundgine.Core.Semantic.Capabilities;
using Foundgine.Core.Semantic.Security;
using Foundgine.Core.Semantic.Security.Execution;
using Foundgine.Core.Semantic.Security.Warrants;
using Foundgine.Core.Semantic.Mutation;
using Foundgine.Core.Semantic.Query;
using Foundgine.Runtime.ControlPlane.AuditLog;
using Foundgine.Runtime.Recovery;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;

namespace Foundgine.Runtime;

/// <summary>
/// Mutations use a fail-closed sequence: resource validation, warrant verification, semantic planning,
/// authorization, invariant derivation, semantic-to-execution lowering, concrete provider certification, replay
/// consumption, provider execution. Port of the Java <c>FoundgineMutationEngine</c>, including the optional
/// write-ahead execution ledger.
/// </summary>
public sealed class FoundgineMutationEngine : IFoundgineMutations
{
    /// <summary>Default lease: how long an execution may stay <c>Started</c> before recovery may treat it as abandoned.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    private readonly IMutationSchema _schema;
    private readonly SemanticModel? _model;
    private readonly ISemanticAuthorizationPolicy _policy;
    private readonly IMutationBatchExecutionProvider _provider;
    private readonly SemanticCapabilityContract _securityContract;
    private readonly ISecurityWarrantKeyResolver? _warrantKeyResolver;
    private readonly string? _expectedWarrantIssuer;
    private readonly ISecurityWarrantReplayStore? _warrantReplayStore;
    private readonly ISecurityWarrantRevocationStore? _warrantRevocationStore;
    private readonly ISecurityWarrantDelegationTrustResolver? _warrantDelegationTrustResolver;
    private readonly SecurityResourceLimits _securityResourceLimits;
    private readonly IAuditLog _auditLog;
    private readonly IMutationExecutionLedger? _ledger;
    private readonly TimeSpan _leaseDuration;
    private readonly MutationRecovery? _recovery;

    /// <summary>Creates the mutation engine.</summary>
    /// <remarks>
    /// <c>auditLog</c> defaults to a new <see cref="InMemoryAuditLog"/>. When <c>ledger</c> is set, executions that
    /// carry an <see cref="ExecutionContextKeys.IdempotencyKey"/> are recorded write-ahead, replayed when already
    /// committed and refused when in doubt; without a ledger the key is ignored. <c>reconciler</c> defaults to the
    /// provider when it implements <see cref="IMutationReconciler"/>. <c>leaseDuration</c> defaults to
    /// <see cref="DefaultLease"/> and must exceed the longest time a provider call may run.
    /// </remarks>
    public FoundgineMutationEngine(
        IMutationSchema schema,
        ISemanticAuthorizationPolicy policy,
        IMutationBatchExecutionProvider provider,
        SemanticModel? model = null,
        ISecurityWarrantKeyResolver? warrantKeyResolver = null,
        string? expectedWarrantIssuer = null,
        ISecurityWarrantReplayStore? warrantReplayStore = null,
        SecurityResourceLimits? securityResourceLimits = null,
        ISecurityWarrantRevocationStore? warrantRevocationStore = null,
        ISecurityWarrantDelegationTrustResolver? warrantDelegationTrustResolver = null,
        IAuditLog? auditLog = null,
        IMutationExecutionLedger? ledger = null,
        IMutationReconciler? reconciler = null,
        TimeSpan? leaseDuration = null)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _model = model;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _securityContract = model is null
            ? new SemanticCapabilityContract(1, [])
            : SemanticCapabilityContractDiscovery.Describe(model, policy);
        SecurityInvariantContractValidator.EnsureContractValid(_securityContract);
        _warrantKeyResolver = warrantKeyResolver;
        _expectedWarrantIssuer = expectedWarrantIssuer;
        _warrantReplayStore = warrantReplayStore;
        _warrantRevocationStore = warrantRevocationStore;
        _warrantDelegationTrustResolver = warrantDelegationTrustResolver;
        _securityResourceLimits = securityResourceLimits ?? new SecurityResourceLimits();
        _securityResourceLimits.Validate();
        _auditLog = auditLog ?? new InMemoryAuditLog();
        _ledger = ledger;
        _leaseDuration = leaseDuration ?? DefaultLease;
        if (_leaseDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must not be negative");
        _recovery = ledger is null
            ? null
            : new MutationRecovery(ledger, reconciler ?? provider as IMutationReconciler, _auditLog);
    }

    /// <summary>Reconciliation and recovery operations, or <c>null</c> when no execution ledger is configured.</summary>
    public MutationRecovery? Recovery => _recovery;

    public MutationDryRunResult DryRun(SemanticMutationRequest request) => DryRun(request, null);

    public MutationDryRunResult DryRun(SemanticMutationRequest request, string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trace = NormalizeCorrelationId(correlationId);
        try
        {
            var plan = AuthorizeAndPlan(request);
            var result = Describe(plan);
            _auditLog.Record(AuditEvent.CreateCorrelated(
                AuditCategory.MutationDryRun, AuditTool,
                request.Security?.Subject ?? UnknownActor, request.Security?.Tenant,
                result.PlanFingerprint, "Mutation dry-run completed", trace));
            return result;
        }
        catch (Exception failure)
        {
            RecordDenied(request, failure, trace);
            throw;
        }
    }

    /// <summary>
    /// Executes a mutation directly after authorization, security-invariant
    /// validation and final execution certification. This is the normal path
    /// for trusted transports such as GraphQL; approval remains an explicit
    /// optional workflow rather than an accidental requirement for every mutation.
    /// </summary>
    public Task<MutationExecutionResult> ExecuteAsync(
        SemanticMutationRequest request,
        ExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        SemanticMutationPlan plan;
        try
        {
            plan = AuthorizeAndPlan(request);
        }
        catch (Exception failure)
        {
            RecordDenied(request, failure, CorrelationIdOf(context));
            throw;
        }

        return ExecutePlanAsync(plan, request.Security, approval: null, context, cancellationToken);
    }

    public MutationPlanApproval Approve(SemanticMutationRequest request, string approvedBy) =>
        Approve(request, approvedBy, null);

    public MutationPlanApproval Approve(SemanticMutationRequest request, string approvedBy, string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        var trace = NormalizeCorrelationId(correlationId);
        var dryRun = DryRun(request, trace);
        var approval = new MutationPlanApproval(
            request,
            Guid.NewGuid().ToString("N"),
            dryRun.PlanFingerprint,
            approvedBy,
            DateTimeOffset.UtcNow);
        _auditLog.Record(AuditEvent.CreateCorrelated(
            AuditCategory.ApprovalDecided, AuditTool, approvedBy, request.Security?.Tenant,
            dryRun.PlanFingerprint, "Mutation approval issued; approval=" + approval.ApprovalId, trace));
        return approval;
    }

    public Task<MutationExecutionResult> ExecuteApprovedAsync(
        MutationPlanApproval approval,
        ExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();

        SemanticMutationPlan plan;
        try
        {
            plan = AuthorizeAndPlan(approval.Request);
            var fingerprint = Fingerprint(plan);
            if (!string.Equals(fingerprint, approval.PlanFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The approved mutation plan changed after approval. Re-run dry-run and obtain a new approval.");
        }
        catch (Exception failure)
        {
            RecordDenied(approval.Request, failure, CorrelationIdOf(context));
            throw;
        }

        return ExecutePlanAsync(plan, approval.Request.Security, approval, context, cancellationToken);
    }

    private const string AuditTool = "semantic_mutation";
    private const string UnknownActor = "anonymous";

    /// <summary>
    /// Everything up to the write-ahead record runs synchronously, so authorization, lowering, certification and trust
    /// failures surface as thrown exceptions exactly as before the ledger existed. Only the ledger and provider
    /// dispatch are asynchronous.
    /// </summary>
    private Task<MutationExecutionResult> ExecutePlanAsync(
        SemanticMutationPlan plan,
        SecurityExecutionContext? security,
        MutationPlanApproval? approval,
        ExecutionContext? context,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var planFingerprint = Fingerprint(plan);
        var ir = new SemanticMutationExecutionLowerer(_schema).Lower(plan);
        var executionContext = context ?? new ExecutionContext();
        executionContext.EnsureWithinDeadline();
        var deadlineCts = executionContext.CreateDeadlineCancellationSource(cancellationToken);

        SecurityWarrantRevocationSnapshot? revocationSnapshot = null;
        string? idempotencyKey;
        try
        {
            // Validated up front so a malformed key is thrown, not returned as a faulted task.
            idempotencyKey = _ledger is null ? null : IdempotencyKeyOf(executionContext);

            var certificate = MutationExecutionSecurityGate.Certify(
                ir,
                _provider,
                _provider.GetType().FullName ?? _provider.GetType().Name,
                plan.RequiredSecurityInvariants.Where(IsEnginePreservedInvariant));

            // The final gate binds the exact lowered IR to the provider and required
            // invariants immediately before any side effect. GraphQL therefore cannot
            // bypass the same mutation security boundary used by MCP/direct callers.
            MutationExecutionSecurityGate.EnsureExecutable(ir, _provider, certificate);

            // Revalidate the complete warrant trust and revocation boundary as late
            // as possible: authorization and IR certification may have taken time,
            // and a warrant can be revoked while those checks are running.
            if (security is not null)
            {
                var dispatchTime = DateTimeOffset.UtcNow;
                VerifyWarrantTrust(security, dispatchTime);
                revocationSnapshot = ValidateWarrantRevocation(security, dispatchTime);
            }
        }
        catch
        {
            deadlineCts.Dispose();
            throw;
        }

        return DispatchAsync(plan, planFingerprint, ir, security, approval, executionContext, deadlineCts,
            revocationSnapshot, idempotencyKey, startedAt, cancellationToken);
    }

    private async Task<MutationExecutionResult> DispatchAsync(
        SemanticMutationPlan plan,
        string planFingerprint,
        ExecutionMutationIR ir,
        SecurityExecutionContext? security,
        MutationPlanApproval? approval,
        ExecutionContext executionContext,
        CancellationTokenSource deadlineCts,
        SecurityWarrantRevocationSnapshot? revocationSnapshot,
        string? idempotencyKey,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        using var deadlineScope = deadlineCts;

        var correlationId = CorrelationIdOf(executionContext);
        var tenant = security?.Tenant;
        var subject = security?.Subject;
        var requestId = Guid.NewGuid().ToString("N");

        // Write-ahead: record the intent before anything irreversible (warrant consumption, provider dispatch).
        // A retry of an already committed key returns here, before the one-time warrant is spent.
        MutationExecutionRecord? active = null;
        if (idempotencyKey is not null)
        {
            var provisional = BuildReceipt(plan, security, approval, requestId, startedAt, startedAt,
                    "pending", 0, correlationId)
                .WithOutcome("pending", "pending", startedAt);
            var begin = await BeginLedgerAsync(idempotencyKey, tenant, subject, planFingerprint, correlationId,
                requestId, startedAt, provisional, cancellationToken).ConfigureAwait(false);
            if (begin.Replay is not null) return begin.Replay;
            active = begin.Active;
        }

        if (security is not null)
        {
            try
            {
                // A warrant is single-use. Consume it only after all checks, including
                // the final trust/revocation check, have passed.
                ConsumeWarrantReplay(security);
            }
            catch
            {
                await AbortUndispatchedAsync(active, "rejected before dispatch").ConfigureAwait(false);
                throw;
            }
        }

        var dispatchContext = DispatchContextOf(executionContext, active);
        MutationBatchResult result;
        try
        {
            result = _provider.ExecuteBatch(ir, dispatchContext, deadlineCts.Token);
        }
        catch (Exception failure)
        {
            var surfaced = await SettleFailureAsync(active, failure).ConfigureAwait(false);
            try
            {
                _auditLog.Record(AuditEvent.CreateCorrelated(
                    AuditCategory.MutationFailed, AuditTool, subject ?? UnknownActor, tenant,
                    Sha256Hex(planFingerprint + "|" + failure.GetType().FullName),
                    "Mutation provider execution failed: " + failure.GetType().Name, correlationId));
            }
            catch (Exception) when (active is not null)
            {
                // The in-doubt exception below is the signal that matters; a broken audit sink must not replace it.
            }

            if (ReferenceEquals(surfaced, failure)) throw;
            throw surfaced;
        }

        var completedAt = DateTimeOffset.UtcNow;
        var resultFingerprint = FingerprintResult(result);
        var receipt = BuildReceipt(plan, security, approval, requestId, startedAt, completedAt,
            resultFingerprint, result.TotalAffectedRows, correlationId);
        var executionResult = new MutationExecutionResult(
            result, planFingerprint, resultFingerprint, approval?.ApprovalId, approval?.ApprovedBy, receipt);

        if (active is not null)
        {
            try
            {
                active = await SettleCommittedAsync(active, receipt,
                    result.Results.Select(x => x.AffectedRows).ToArray()).ConfigureAwait(false);
            }
            catch (Exception ledgerFailure)
            {
                throw new MutationCommittedUnrecordedException(executionResult, ledgerFailure);
            }
        }

        executionContext.EnsureWithinDeadline();
        if (security is not null && revocationSnapshot is { } snapshot)
        {
            var finalSnapshot = ValidateWarrantRevocation(security, DateTimeOffset.UtcNow);
            snapshot.AssertUnchanged(finalSnapshot);
        }

        // Audit persistence is part of the execution acknowledgement: if a durable sink is
        // configured and append fails, do not report a successful mutation response.
        try
        {
            _auditLog.Record(AuditEvent.CreateCorrelated(
                AuditCategory.MutationSucceeded, AuditTool, subject ?? UnknownActor, tenant, resultFingerprint,
                "Mutation completed; receipt=" + receipt.RequestId, correlationId));
        }
        catch (Exception auditFailure)
        {
            throw new MutationAuditPersistenceException(executionResult, auditFailure);
        }

        if (active is not null)
        {
            try
            {
                await _ledger!.TryUpdateAsync(active.Version, active.WithAuditAcknowledged(DateTimeOffset.UtcNow))
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Recovery re-appends the audit event for an unacknowledged record (at-least-once).
            }
        }

        return executionResult;
    }

    /// <summary>The request id is engine-owned: a caller-supplied value is removed, and one is added only for ledgered runs.</summary>
    private static ExecutionContext DispatchContextOf(ExecutionContext context, MutationExecutionRecord? active)
    {
        var values = new Dictionary<string, object?>(context.EffectiveValues, StringComparer.Ordinal);
        values.Remove(ExecutionContextKeys.ExecutionRequestId);
        if (active is not null) values[ExecutionContextKeys.ExecutionRequestId] = active.RequestId;
        return new ExecutionContext(values) { DeadlineUtc = context.DeadlineUtc };
    }

    private sealed record LedgerBegin(MutationExecutionResult? Replay, MutationExecutionRecord? Active);

    private static string? IdempotencyKeyOf(ExecutionContext context)
    {
        if (!context.EffectiveValues.TryGetValue(ExecutionContextKeys.IdempotencyKey, out var value) || value is null)
            return null;
        if (value is string text && MutationExecutionRecord.IsSafeId(text)) return text;
        throw new ArgumentException("The idempotency key must contain 1-128 safe identifier characters.");
    }

    private async Task<LedgerBegin> BeginLedgerAsync(string key, string? tenant, string? subject,
        string planFingerprint, string? correlationId, string requestId, DateTimeOffset startedAt,
        ExecutionReceipt provisional, CancellationToken cancellationToken)
    {
        var ledger = _ledger!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var fresh = new MutationExecutionRecord(tenant, subject, key, requestId, planFingerprint, correlationId,
                MutationExecutionState.Started, 1, 0, startedAt, startedAt, startedAt + _leaseDuration,
                provisional, [], false, null);
            var created = await ledger.TryCreateAsync(fresh, cancellationToken).ConfigureAwait(false);
            if (created.Created) return new LedgerBegin(null, created.Record);

            var existing = created.Record;
            if (!string.Equals(existing.PlanFingerprint, planFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different mutation plan.");

            switch (existing.State)
            {
                case MutationExecutionState.Committed:
                {
                    var original = existing.Receipt;
                    var replay = new MutationExecutionResult(
                        new MutationBatchResult(existing.AffectedRows.Select(n => new MutationResult(n)).ToArray()),
                        planFingerprint, original.ResultFingerprint, original.ApprovalId, original.ApprovedBy,
                        original);
                    return new LedgerBegin(replay, null);
                }
                case MutationExecutionState.Aborted:
                {
                    var reopened = existing.Reopen(requestId, correlationId, now, now + _leaseDuration, provisional);
                    var stored = await ledger.TryUpdateAsync(existing.Version, reopened, cancellationToken)
                        .ConfigureAwait(false);
                    if (stored is not null) return new LedgerBegin(null, stored);
                    break; // another writer moved first; re-read
                }
                case MutationExecutionState.Started:
                    if (existing.LeaseExpiresAt is { } lease && now < lease)
                        throw new MutationInProgressException(existing.RequestId, key);
                    await ledger.TryUpdateAsync(existing.Version, existing.Transition(
                        MutationExecutionState.InDoubt, now, existing.Receipt, existing.AffectedRows,
                        "lease expired without a recorded outcome"), cancellationToken).ConfigureAwait(false);
                    throw new MutationInDoubtException(existing.RequestId, key);
                case MutationExecutionState.InDoubt:
                    throw new MutationInDoubtException(existing.RequestId, key);
            }
        }

        throw new MutationInProgressException(string.Empty, key);
    }

    private async Task<MutationExecutionRecord> SettleAsync(MutationExecutionRecord record,
        MutationExecutionState next, ExecutionReceipt receipt, IReadOnlyList<int> rows, string resolution)
    {
        var updated = record.Transition(next, DateTimeOffset.UtcNow, receipt, rows, resolution);
        return await _ledger!.TryUpdateAsync(record.Version, updated).ConfigureAwait(false)
            ?? throw new MutationLedgerException("The execution record changed concurrently.");
    }

    /// <summary>
    /// Records a commit the provider has confirmed. Recovery may have moved the record while the provider was running
    /// (lease expiry), so a version conflict is re-read: the same attempt in <c>Started</c> or <c>InDoubt</c> is
    /// committed, one that recovery already committed is accepted. <c>Aborted</c> or a different attempt means the
    /// provider's fencing guarantee was broken, which is surfaced and never overwritten.
    /// </summary>
    private async Task<MutationExecutionRecord> SettleCommittedAsync(MutationExecutionRecord record,
        ExecutionReceipt receipt, IReadOnlyList<int> rows)
    {
        var current = record;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await _ledger!.TryUpdateAsync(current.Version,
                current.Transition(MutationExecutionState.Committed, DateTimeOffset.UtcNow, receipt, rows, "executed"))
                .ConfigureAwait(false);
            if (stored is not null) return stored;

            current = await _ledger.FindAsync(record.Tenant, record.Subject, record.IdempotencyKey).ConfigureAwait(false)
                ?? throw new MutationLedgerException("The execution record disappeared.");
            if (!string.Equals(current.RequestId, record.RequestId, StringComparison.Ordinal)
                || current.State == MutationExecutionState.Aborted)
                throw new MutationLedgerException("The provider reported a commit for an attempt the ledger had already"
                    + " closed as not applied. Check the provider's reconciliation fencing.");
            if (current.State == MutationExecutionState.Committed) return current;
        }

        throw new MutationLedgerException("The execution record kept changing.");
    }

    /// <summary>Nothing was dispatched, so the intent is withdrawn and the key stays usable. Best effort.</summary>
    private async Task AbortUndispatchedAsync(MutationExecutionRecord? active, string why)
    {
        if (active is null) return;
        try
        {
            await SettleAsync(active, MutationExecutionState.Aborted,
                active.Receipt.WithOutcome("aborted", "aborted", DateTimeOffset.UtcNow), [], why).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the Started record expires into InDoubt; recovery will find the provider has nothing
        }
    }

    /// <summary>Settles a failed dispatch: explicit "not applied" aborts; anything else leaves the outcome in doubt.</summary>
    private async Task<Exception> SettleFailureAsync(MutationExecutionRecord? active, Exception failure)
    {
        if (active is null) return failure;
        var notApplied = failure is MutationNotAppliedException;
        try
        {
            var outcome = notApplied ? "aborted" : "in_doubt";
            await SettleAsync(active,
                notApplied ? MutationExecutionState.Aborted : MutationExecutionState.InDoubt,
                active.Receipt.WithOutcome(outcome, outcome, DateTimeOffset.UtcNow), [],
                notApplied ? "provider reported not applied" : "provider failed after dispatch").ConfigureAwait(false);
        }
        catch (Exception)
        {
            // still Started; the lease turns it into InDoubt
        }

        return notApplied ? failure : new MutationInDoubtException(active.RequestId, active.IdempotencyKey, failure);
    }

    private ExecutionReceipt BuildReceipt(
        SemanticMutationPlan plan,
        SecurityExecutionContext? security,
        MutationPlanApproval? approval,
        string requestId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string resultFingerprint,
        int totalAffectedRows,
        string? correlationId)
    {
        var planFingerprint = Fingerprint(plan);
        var providerName = _provider.GetType().FullName ?? _provider.GetType().Name;
        var authorizationFingerprint = Sha256Hex(
            (_policy.GetType().FullName ?? _policy.GetType().Name) + "|" + planFingerprint + "|"
            + string.Join(",", plan.RequiredSecurityInvariants) + "|"
            + (security is null ? "anonymous-context" : security.AuthorityCachePartition));
        var invariantDigest = Sha256Hex(string.Join("|", plan.RequiredSecurityInvariants));
        var evidence = new ExecutionEvidence(
            providerName,
            planFingerprint,
            // EntityId is an unsigned 64-bit hash; receipts carry 32-bit node ids, so keep the low 32 bits (never throw).
            plan.Operations.Select(operation => unchecked((int)operation.Entity.Value)).Distinct().OrderBy(x => x).ToArray(),
            totalAffectedRows,
            Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds),
            Sha256Hex(providerName + "|" + planFingerprint),
            Sha256Hex("mutation-intent-v1|" + planFingerprint),
            authorizationFingerprint,
            security?.Warrant.Id,
            security?.Warrant.Digest,
            invariantDigest,
            null);
        var versions = _model is null
            ? new SemanticVersionSet("unversioned", 1, 1, 1, 1)
            : SemanticVersionSet.For(_model);
        return ExecutionReceiptFactory.Create(
            requestId,
            evidence,
            resultFingerprint,
            evidence.AuthorizedNodeIds,
            Describe(plan).Effects,
            startedAt,
            completedAt,
            versions.CapabilityContractVersion,
            versions.CapabilityVersion,
            versions.IntentVersion,
            versions.PlanVersion,
            versions.SemanticModelVersion,
            approval?.ApprovalId,
            approval?.ApprovedBy,
            approval?.ApprovedAt) with { CorrelationId = correlationId };
    }

    private static string NormalizeCorrelationId(string? value) =>
        MutationExecutionRecord.IsSafeId(value) ? value! : Guid.NewGuid().ToString("N");

    private static string CorrelationIdOf(ExecutionContext? context)
    {
        if (context is not null
            && context.EffectiveValues.TryGetValue(ExecutionContextKeys.CorrelationId, out var value)
            && value is string text && MutationExecutionRecord.IsSafeId(text))
            return text;
        return Guid.NewGuid().ToString("N");
    }

    private void RecordDenied(SemanticMutationRequest request, Exception failure, string correlationId) =>
        _auditLog.Record(AuditEvent.CreateCorrelated(
            AuditCategory.MutationDenied, AuditTool,
            request.Security?.Subject ?? UnknownActor, request.Security?.Tenant,
            Sha256Hex(failure.GetType().FullName + "|" + failure.Message),
            "Mutation rejected before execution: " + failure.GetType().Name, correlationId));

    private void ValidateWarrant(SemanticMutationRequest request)
    {
        var security = request.Security;
        if (security is null)
            return;

        if (_warrantKeyResolver is null)
            throw new InvalidOperationException(
                "A security warrant was supplied, but no warrant key resolver is configured.");

        var now = DateTimeOffset.UtcNow;
        VerifyWarrantTrust(security, now);
        ValidateWarrantRevocation(security, now);

        if (!StringComparer.Ordinal.Equals(security.Warrant.Subject, security.Subject))
            throw new UnauthorizedAccessException(
                "The warrant does not authorize the requested subject.");

        var allowedTenants = security.Warrant.Constraints.AllowedTenants;
        if (allowedTenants.Count > 0 &&
            (security.Tenant is null || !allowedTenants.Contains(security.Tenant, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException(
                "The warrant does not authorize the requested tenant scope.");

        foreach (var operation in request.Graph.Operations)
        {
            var capability = _securityContract.Capabilities.FirstOrDefault(c =>
                c.TargetEntityId == operation.Entity &&
                string.Equals(c.Operation, operation.Kind.ToString().ToLowerInvariant(), StringComparison.Ordinal));

            if (capability is null)
                throw new UnauthorizedAccessException(
                    $"No security capability contract exists for entity '{operation.Entity}' and operation '{operation.Kind}'.");

            if (!SecurityWarrantAuthorization.Allows(
                    security.Warrant,
                    security.Subject,
                    security.Audience,
                    capability.Id,
                    capability.Operation,
                    security.Tenant,
                    security.ResourceScope))
            {
                throw new UnauthorizedAccessException(
                    $"Security warrant does not authorize capability '{capability.Id}' for subject '{security.Subject}'.");
            }

            ValidateWarrantAllowedFields(operation, security);
        }
    }

    private void VerifyWarrantTrust(SecurityExecutionContext security, DateTimeOffset now)
    {
        var warrant = security.Warrant;
        var isDelegated = warrant.ParentId is not null || warrant.ParentDigest is not null ||
                          warrant.DelegationPath.Count != 0;

        if (isDelegated || security.DelegationChain is not null)
        {
            SecurityWarrantExecutionTrust.Verify(
                warrant,
                _warrantKeyResolver!,
                _expectedWarrantIssuer ?? throw new InvalidOperationException(
                    "Delegated warrant execution requires an explicitly configured root issuer."),
                security.Audience,
                now,
                security.DelegationChain,
                _warrantDelegationTrustResolver,
                security.Tenant);
            return;
        }

        SecurityWarrantVerifier.Verify(
            warrant, _warrantKeyResolver!, now, _expectedWarrantIssuer, security.Audience);
    }

    private SecurityWarrantRevocationSnapshot ValidateWarrantRevocation(
        SecurityExecutionContext security,
        DateTimeOffset now)
    {
        if (_warrantRevocationStore is null)
            throw new InvalidOperationException(
                "Warrant-backed mutation discovery and execution require a warrant revocation store; refusing to run without current revocation state.");

        return SecurityWarrantRevocationGuard.Validate(
            security.Warrant, _warrantRevocationStore, now);
    }

    private void ValidateWarrantAllowedFields(
        SemanticMutationOperation operation,
        SecurityExecutionContext security)
    {
        var allowed = security.Warrant.Constraints.AllowedFields;
        if (allowed.Count == 0)
            return;

        if (_model is null)
            throw new InvalidOperationException(
                "A mutation warrant specifies allowed fields, but no SemanticModel is configured to resolve them.");

        var entity = _model.Get(operation.Entity);
        var requested = operation.Fields
            .Where(field => field.Source is null)
            .Select(field => entity.Fields.FirstOrDefault(x => x.Id == field.Field))
            .Where(field => field is not null)
            .Select(field => field!.Name)
            .Concat(operation.ReturnFields
                .Select(field => entity.Fields.FirstOrDefault(x => x.Id == field))
                .Where(field => field is not null)
                .Select(field => field!.Name))
            .Concat(FilterFields(operation.Filter, entity))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Any(field => !allowed.Contains(field, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException(
                $"Security warrant does not authorize one or more requested mutation fields on '{entity.Name}'.");
    }

    private static IEnumerable<string> FilterFields(
        SemanticFilterExpression? filter,
        SemanticEntity entity)
    {
        switch (filter)
        {
            case SemanticFieldFilter field:
                var semanticField = entity.Fields.FirstOrDefault(x => x.Id == field.Field);
                if (semanticField is not null)
                    yield return semanticField.Name;
                yield break;

            case SemanticAndFilter and:
                foreach (var expression in and.Expressions)
                foreach (var name in FilterFields(expression, entity))
                    yield return name;
                yield break;

            case SemanticOrFilter or:
                foreach (var expression in or.Expressions)
                foreach (var name in FilterFields(expression, entity))
                    yield return name;
                yield break;

            default:
                yield break;
        }
    }

    private void ConsumeWarrantReplay(SecurityExecutionContext security)
    {
        if (_warrantReplayStore is null)
            throw new InvalidOperationException("Executing a warrant-backed mutation requires a warrant replay store.");

        SecurityWarrantReplayGuard.Consume(
            security.Warrant,
            _warrantReplayStore,
            DateTimeOffset.UtcNow);
    }

    private SemanticMutationPlan AuthorizeAndPlan(SemanticMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MutationSecurityResourceLimitValidator.Validate(request, _securityResourceLimits);
        ValidateWarrant(request);
        var semanticPlan = new SemanticMutationPlanner().Plan(request.Graph);
        var authorizedPlan = new MutationAuthorizer(_schema, _policy).Authorize(semanticPlan);
        var requiredSecurityInvariants = RequiredSecurityInvariantsFor(authorizedPlan);

        // Authorization is applied to the exact semantic representation that is
        // subsequently lowered into ExecutionMutationIR. No independently planned
        // batch is discarded or allowed to become an alternate execution source.
        return authorizedPlan with
        {
            RequiredSecurityInvariants = requiredSecurityInvariants
        };
    }

    private IReadOnlyList<string> RequiredSecurityInvariantsFor(SemanticMutationPlan plan)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in plan.Operations)
        {
            var capability = _securityContract.Capabilities.FirstOrDefault(c =>
                c.TargetEntityId == operation.Entity &&
                string.Equals(c.Operation, operation.Kind.ToString().ToLowerInvariant(), StringComparison.Ordinal));

            if (capability is null)
                throw new InvalidOperationException(
                    $"No security capability contract exists for entity '{operation.Entity}' and operation '{operation.Kind}'.");

            foreach (var invariant in capability.EffectiveSecurityInvariants)
                required.Add(invariant);
        }

        if (required.Count == 0)
            throw new InvalidOperationException(
                "A mutation execution cannot proceed without explicit security invariants.");

        return required.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static bool IsEnginePreservedInvariant(string id) => id switch
    {
        SecurityInvariantIds.AuthorizationRequired => true,
        SecurityInvariantIds.RuntimeAuthorization => true,
        SecurityInvariantIds.FieldVisibility => true,
        SecurityInvariantIds.RelationshipVisibility => true,
        _ => false
    };

    private MutationDryRunResult Describe(SemanticMutationPlan plan) =>
        new(
            Fingerprint(plan),
            plan.Operations.Select((x, i) => new MutationPlanOperation(
                i,
                _schema.GetEntity(x.Entity).Name,
                x.Kind.ToString(),
                x.Fields.Select(f => f.Field.Value.ToString()).ToArray(),
                x.ReturnFields.Select(f => f.Value.ToString()).ToArray())).ToArray(),
            plan.Operations.SelectMany(x => x.Effects).Select(FormatEffect).ToArray());

    private string FormatEffect(SemanticMutationEffect effect) =>
        $"{effect.Kind}:{_schema.GetEntity(effect.Entity).Name}" +
        (effect.Field is { } field ? $".{field.Value}" : string.Empty);

    private static string Fingerprint(SemanticMutationPlan plan)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            version = "mutation-plan-v1",
            operations = plan.Operations,
            dependencies = plan.Dependencies,
            security = plan.RequiredSecurityInvariants
        });
        return Sha256Hex(canonical);
    }

    private static string FingerprintResult(MutationBatchResult result)
    {
        var canonical = JsonSerializer.Serialize(result.Results.Select(x => new
        {
            x.AffectedRows,
            returned = x.ReturnedValues?.OrderBy(p => p.Key.Value)
                .Select(p => new { field = p.Key.Value, value = p.Value })
        }));
        return Sha256Hex(canonical);
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
