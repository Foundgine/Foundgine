package com.foundgine.runtime;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.foundgine.core.abstractions.*;
import com.foundgine.core.execution.CancellationToken;
import com.foundgine.core.execution.ExecutionContextKeys;
import com.foundgine.core.execution.ExecutionContext;
import com.foundgine.core.execution.ExecutionEvidence;
import com.foundgine.core.execution.ExecutionReceipt;
import com.foundgine.core.execution.ExecutionReceiptFactory;
import com.foundgine.core.execution.mutation.*;
import com.foundgine.core.semantic.*;
import com.foundgine.core.semantic.authorization.*;
import com.foundgine.core.semantic.capabilities.*;
import com.foundgine.core.semantic.mutation.*;
import com.foundgine.core.semantic.planning.mutation.*;
import com.foundgine.core.semantic.query.*;
import com.foundgine.core.semantic.security.*;
import com.foundgine.core.semantic.security.execution.*;
import com.foundgine.core.semantic.security.warrants.*;
import com.foundgine.runtime.controlplane.auditlog.AuditEvent;
import com.foundgine.runtime.controlplane.auditlog.IAuditLog;
import com.foundgine.runtime.controlplane.auditlog.InMemoryAuditLog;
import com.foundgine.runtime.recovery.*;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;

/**
 * Port of {@code Foundgine.Runtime.FoundgineMutationEngine}.
 *
 * <p>Mutations use the same fail-closed sequence as the C# implementation: resource validation ->
 * warrant verification -> semantic planning -> authorization -> invariant derivation ->
 * semantic-to-execution lowering -> concrete provider certification -> replay consumption ->
 * provider execution.
 */
public final class FoundgineMutationEngine implements IFoundgineMutations {
    private static final ObjectMapper CANONICAL = new ObjectMapper()
            .enable(com.fasterxml.jackson.databind.SerializationFeature.ORDER_MAP_ENTRIES_BY_KEYS);

    private final MutationSchema schema;
    private final SemanticModel model;
    private final ISemanticAuthorizationPolicy policy;
    private final IMutationBatchExecutionProvider provider;
    private final SemanticCapabilityContract securityContract;
    private final ISecurityWarrantKeyResolver warrantKeyResolver;
    private final String expectedWarrantIssuer;
    private final ISecurityWarrantReplayStore warrantReplayStore;
    private final ISecurityWarrantRevocationStore warrantRevocationStore;
    private final ISecurityWarrantDelegationTrustResolver warrantDelegationTrustResolver;
    private final SecurityResourceLimits securityResourceLimits;
    private final IAuditLog auditLog;
    private final IMutationExecutionLedger ledger;
    private final java.time.Duration leaseDuration;
    private final MutationRecovery recovery;

    /** Default lease: how long an execution may stay STARTED before recovery may treat it as abandoned. */
    public static final java.time.Duration DEFAULT_LEASE = java.time.Duration.ofMinutes(5);

    public FoundgineMutationEngine(
            MutationSchema schema,
            ISemanticAuthorizationPolicy policy,
            IMutationBatchExecutionProvider provider) {
        this(schema, policy, provider, null, null, null, null, null, null, null);
    }

    public FoundgineMutationEngine(
            MutationSchema schema,
            ISemanticAuthorizationPolicy policy,
            IMutationBatchExecutionProvider provider,
            SemanticModel model,
            ISecurityWarrantKeyResolver warrantKeyResolver,
            String expectedWarrantIssuer,
            ISecurityWarrantReplayStore warrantReplayStore,
            SecurityResourceLimits securityResourceLimits) {
        this(schema, policy, provider, model, warrantKeyResolver, expectedWarrantIssuer,
                warrantReplayStore, securityResourceLimits, null, null);
    }

    public FoundgineMutationEngine(
            MutationSchema schema,
            ISemanticAuthorizationPolicy policy,
            IMutationBatchExecutionProvider provider,
            SemanticModel model,
            ISecurityWarrantKeyResolver warrantKeyResolver,
            String expectedWarrantIssuer,
            ISecurityWarrantReplayStore warrantReplayStore,
            SecurityResourceLimits securityResourceLimits,
            ISecurityWarrantRevocationStore warrantRevocationStore,
            ISecurityWarrantDelegationTrustResolver warrantDelegationTrustResolver) {
        this(schema, policy, provider, model, warrantKeyResolver, expectedWarrantIssuer,
                warrantReplayStore, securityResourceLimits, warrantRevocationStore,
                warrantDelegationTrustResolver, new InMemoryAuditLog());
    }

    public FoundgineMutationEngine(
            MutationSchema schema, ISemanticAuthorizationPolicy policy,
            IMutationBatchExecutionProvider provider, SemanticModel model,
            ISecurityWarrantKeyResolver warrantKeyResolver, String expectedWarrantIssuer,
            ISecurityWarrantReplayStore warrantReplayStore, SecurityResourceLimits securityResourceLimits,
            ISecurityWarrantRevocationStore warrantRevocationStore,
            ISecurityWarrantDelegationTrustResolver warrantDelegationTrustResolver,
            IAuditLog auditLog) {
        this(schema, policy, provider, model, warrantKeyResolver, expectedWarrantIssuer, warrantReplayStore,
                securityResourceLimits, warrantRevocationStore, warrantDelegationTrustResolver, auditLog,
                null, null, null);
    }

    /**
     * Full constructor. When {@code ledger} is non-null, executions that carry an
     * {@code ExecutionContextKeys.IDEMPOTENCY_KEY} are recorded write-ahead, replayed when already committed and
     * refused when in doubt. {@code reconciler} defaults to the provider when it implements
     * {@link IMutationReconciler}; {@code leaseDuration} defaults to {@link #DEFAULT_LEASE} and should exceed the
     * longest time a provider call may run.
     */
    public FoundgineMutationEngine(
            MutationSchema schema, ISemanticAuthorizationPolicy policy,
            IMutationBatchExecutionProvider provider, SemanticModel model,
            ISecurityWarrantKeyResolver warrantKeyResolver, String expectedWarrantIssuer,
            ISecurityWarrantReplayStore warrantReplayStore, SecurityResourceLimits securityResourceLimits,
            ISecurityWarrantRevocationStore warrantRevocationStore,
            ISecurityWarrantDelegationTrustResolver warrantDelegationTrustResolver,
            IAuditLog auditLog, IMutationExecutionLedger ledger, IMutationReconciler reconciler,
            java.time.Duration leaseDuration) {
        this.schema = Objects.requireNonNull(schema, "schema");
        this.model = model;
        this.policy = Objects.requireNonNull(policy, "policy");
        this.provider = Objects.requireNonNull(provider, "provider");
        this.securityContract =
                model == null
                        ? new SemanticCapabilityContract(1, List.of())
                        : SemanticCapabilityContractDiscovery.describe(model, policy);
        SecurityInvariantContractValidator.ensureContractValid(securityContract);
        this.warrantKeyResolver = warrantKeyResolver;
        this.expectedWarrantIssuer = expectedWarrantIssuer;
        this.warrantReplayStore = warrantReplayStore;
        this.warrantRevocationStore = warrantRevocationStore;
        this.warrantDelegationTrustResolver = warrantDelegationTrustResolver;
        this.securityResourceLimits =
                securityResourceLimits != null
                        ? securityResourceLimits
                        : new SecurityResourceLimits();
        this.securityResourceLimits.validate();
        this.auditLog = Objects.requireNonNull(auditLog, "auditLog");
        this.ledger = ledger;
        this.leaseDuration = leaseDuration == null ? DEFAULT_LEASE : leaseDuration;
        if (this.leaseDuration.isNegative()) throw new IllegalArgumentException("leaseDuration must not be negative");
        IMutationReconciler effective = reconciler != null ? reconciler
                : provider instanceof IMutationReconciler r ? r : null;
        this.recovery = ledger == null ? null : new MutationRecovery(ledger, effective, auditLog);
    }

    /** Reconciliation and recovery operations, or empty when no execution ledger is configured. */
    @Override
    public java.util.Optional<MutationRecovery> recovery() { return java.util.Optional.ofNullable(recovery); }

    @Override
    public MutationDryRunResult dryRun(SemanticMutationRequest request) {
        return dryRun(request, null);
    }

    @Override
    public MutationDryRunResult dryRun(SemanticMutationRequest request, String correlationId) {
        Objects.requireNonNull(request, "request");
        String trace = normalizeCorrelationId(correlationId);
        try {
            SemanticMutationPlan plan = authorizeAndPlan(request);
            MutationDryRunResult result = describe(plan);
            auditLog.record(AuditEvent.createCorrelated(
                    AuditEvent.AuditCategory.MUTATION_DRY_RUN, "semantic_mutation",
                    request.security() == null ? null : request.security().subject(),
                    request.security() == null ? null : request.security().tenant(),
                    result.planFingerprint(), "Mutation dry-run completed", trace));
            return result;
        } catch (RuntimeException failure) {
            recordDenied(request, failure, null, trace);
            throw failure;
        }
    }

    @Override
    public CompletionStage<MutationExecutionResult> executeAsync(
            SemanticMutationRequest request,
            ExecutionContext context,
            CancellationToken cancellationToken) {
        Objects.requireNonNull(request, "request");
        if (cancellationToken != null) cancellationToken.throwIfCancellationRequested();
        SemanticMutationPlan plan;
        try {
            plan = authorizeAndPlan(request);
        } catch (RuntimeException failure) {
            recordDenied(request, failure, context);
            throw failure;
        }
        return executePlan(plan, request.security(), null, context, cancellationToken);
    }

    @Override
    public MutationPlanApproval approve(SemanticMutationRequest request, String approvedBy) {
        return approve(request, approvedBy, null);
    }

    @Override
    public MutationPlanApproval approve(SemanticMutationRequest request, String approvedBy, String correlationId) {
        Objects.requireNonNull(request, "request");
        if (approvedBy == null || approvedBy.isBlank())
            throw new IllegalArgumentException("approvedBy must not be null or blank");
        String trace = normalizeCorrelationId(correlationId);
        MutationDryRunResult dryRun = dryRun(request, trace);
        MutationPlanApproval approval = new MutationPlanApproval(
                request, UUID.randomUUID().toString().replace("-", ""),
                dryRun.planFingerprint(), approvedBy, Instant.now());
        auditLog.record(AuditEvent.createCorrelated(
                AuditEvent.AuditCategory.APPROVAL_DECIDED, "semantic_mutation",
                approvedBy, request.security() == null ? null : request.security().tenant(),
                dryRun.planFingerprint(), "Mutation approval issued; approval=" + approval.approvalId(),
                trace));
        return approval;
    }

    @Override
    public CompletionStage<MutationExecutionResult> executeApprovedAsync(
            MutationPlanApproval approval,
            ExecutionContext context,
            CancellationToken cancellationToken) {
        Objects.requireNonNull(approval, "approval");
        if (cancellationToken != null) cancellationToken.throwIfCancellationRequested();

        SemanticMutationPlan plan;
        try {
            plan = authorizeAndPlan(approval.request());
            String fingerprint = fingerprint(plan);
            if (!fingerprint.equals(approval.planFingerprint()))
                throw new IllegalStateException(
                        "The approved mutation plan changed after approval. Re-run dry-run and obtain a"
                                + " new approval.");
        } catch (RuntimeException failure) {
            recordDenied(approval.request(), failure, context);
            throw failure;
        }

        return executePlan(
                plan, approval.request().security(), approval, context, cancellationToken);
    }

    private CompletionStage<MutationExecutionResult> executePlan(
            SemanticMutationPlan plan,
            com.foundgine.core.semantic.security.execution.SecurityExecutionContext security,
            MutationPlanApproval approval,
            ExecutionContext context,
            CancellationToken cancellationToken) {
        Instant startedAt = Instant.now();
        String planFingerprint = fingerprint(plan);
        ExecutionMutationIR ir = new SemanticMutationExecutionLowerer(schema).lower(plan);
        ExecutionContext executionContext = context != null ? context : ExecutionContext.EMPTY;
        executionContext.ensureWithinDeadline();

        try (var deadlineSource =
                executionContext.createDeadlineCancellationSource(
                        cancellationToken != null ? cancellationToken : CancellationToken.NONE)) {
            MutationExecutionSecurityCertificate certificate =
                    MutationExecutionSecurityGate.certify(
                            ir,
                            provider,
                            provider.getClass().getName(),
                            plan.requiredSecurityInvariants().stream()
                                    .filter(FoundgineMutationEngine::isEnginePreservedInvariant)
                                    .toList());
            MutationExecutionSecurityGate.ensureExecutable(ir, provider, certificate);

            SecurityWarrantRevocationSnapshot revocationSnapshot = null;
            if (security != null) {
                Instant dispatchTime = Instant.now();
                verifyWarrantTrust(security, dispatchTime);
                revocationSnapshot = validateWarrantRevocation(security, dispatchTime);
            }

            String correlationId = correlationId(executionContext);
            String tenant = security == null ? null : security.tenant();
            String subject = security == null ? null : security.subject();
            String requestId = UUID.randomUUID().toString().replace("-", "");

            // Write-ahead: record the intent before anything irreversible (warrant consumption, provider dispatch).
            // A retry of an already committed key returns here, before the one-time warrant is spent.
            MutationExecutionRecord active = null;
            String idempotencyKey = ledger == null ? null : idempotencyKey(executionContext);
            if (idempotencyKey != null) {
                ExecutionReceipt provisional = buildReceipt(plan, security, approval, requestId, startedAt, startedAt,
                        "pending", 0, correlationId).withOutcome("pending", "pending", startedAt);
                LedgerBegin begin = beginLedger(idempotencyKey, tenant, subject, planFingerprint, correlationId,
                        requestId, startedAt, provisional);
                if (begin.replay() != null) return CompletableFuture.completedFuture(begin.replay());
                active = begin.active();
            }

            if (security != null) {
                try {
                    // Consume only after the final trust and revocation checks have passed.
                    consumeWarrantReplay(security);
                } catch (RuntimeException failure) {
                    abortUndispatched(active, "rejected before dispatch");
                    throw failure;
                }
            }

            ExecutionContext dispatchContext = dispatchContext(executionContext, active);
            MutationBatchResult result;
            try {
                result = provider.executeBatch(ir, dispatchContext, deadlineSource.token());
            } catch (RuntimeException failure) {
                RuntimeException surfaced = settleFailure(active, failure);
                try {
                    auditLog.record(AuditEvent.createCorrelated(
                            AuditEvent.AuditCategory.MUTATION_FAILED, "semantic_mutation",
                            subject, tenant,
                            sha256Hex(planFingerprint + "|" + failure.getClass().getName()),
                            "Mutation provider execution failed: " + failure.getClass().getSimpleName(), correlationId));
                } catch (RuntimeException auditFailure) {
                    if (active == null) throw auditFailure;
                    surfaced.addSuppressed(auditFailure);
                }
                throw surfaced;
            }
            Instant completedAt = Instant.now();
            String resultFingerprint = fingerprintResult(result);
            ExecutionReceipt receipt = buildReceipt(plan, security, approval, requestId, startedAt, completedAt,
                    resultFingerprint, result.totalAffectedRows(), correlationId);
            MutationExecutionResult executionResult = new MutationExecutionResult(
                    result, planFingerprint, resultFingerprint,
                    approval != null ? approval.approvalId() : null,
                    approval != null ? approval.approvedBy() : null, receipt);
            if (active != null) {
                try {
                    active = settleCommitted(active, receipt,
                            result.results().stream().map(MutationResult::affectedRows).toList());
                } catch (RuntimeException ledgerFailure) {
                    throw new MutationCommittedUnrecordedException(executionResult, ledgerFailure);
                }
            }
            executionContext.ensureWithinDeadline();
            if (security != null && revocationSnapshot != null) {
                SecurityWarrantRevocationSnapshot finalSnapshot =
                        validateWarrantRevocation(security, Instant.now());
                revocationSnapshot.assertUnchanged(finalSnapshot);
            }
            // Audit persistence is part of the execution acknowledgement: if a durable sink is
            // configured and append fails, do not report a successful mutation response.
            try {
                auditLog.record(AuditEvent.createCorrelated(
                        AuditEvent.AuditCategory.MUTATION_SUCCEEDED, "semantic_mutation",
                        subject, tenant, resultFingerprint,
                        "Mutation completed; receipt=" + receipt.requestId(), correlationId));
            } catch (RuntimeException auditFailure) {
                throw new MutationAuditPersistenceException(executionResult, auditFailure);
            }
            if (active != null) {
                try {
                    ledger.tryUpdate(active.version(), active.withAuditAcknowledged(Instant.now()));
                } catch (RuntimeException ignored) {
                    // Recovery re-appends the audit event for an unacknowledged record (at-least-once).
                }
            }
            return CompletableFuture.completedFuture(executionResult);
        }
    }

    /** The request id is engine-owned: a caller-supplied value is removed, and one is added only for ledgered runs. */
    private static ExecutionContext dispatchContext(ExecutionContext context, MutationExecutionRecord active) {
        var values = new LinkedHashMap<String, Object>(context.effectiveValues());
        values.remove(ExecutionContextKeys.EXECUTION_REQUEST_ID);
        if (active != null) values.put(ExecutionContextKeys.EXECUTION_REQUEST_ID, active.requestId());
        return new ExecutionContext(Collections.unmodifiableMap(values), context.deadlineUtc());
    }

    private record LedgerBegin(MutationExecutionResult replay, MutationExecutionRecord active) {}

    private static String idempotencyKey(ExecutionContext context) {
        Object value = context.effectiveValues().get(ExecutionContextKeys.IDEMPOTENCY_KEY);
        if (value == null) return null;
        if (value instanceof String text && text.matches(MutationExecutionRecord.SAFE_ID)) return text;
        throw new IllegalArgumentException("The idempotency key must contain 1-128 safe identifier characters.");
    }

    private LedgerBegin beginLedger(String key, String tenant, String subject, String planFingerprint,
            String correlationId, String requestId, Instant startedAt, ExecutionReceipt provisional) {
        for (int attempt = 0; attempt < 3; attempt++) {
            Instant now = Instant.now();
            var fresh = new MutationExecutionRecord(tenant, subject, key, requestId, planFingerprint, correlationId,
                    MutationExecutionState.STARTED, 1, 0, startedAt, startedAt, startedAt.plus(leaseDuration),
                    provisional, List.of(), false, null);
            var created = ledger.tryCreate(fresh);
            if (created.created()) return new LedgerBegin(null, created.record());
            MutationExecutionRecord existing = created.record();
            if (!existing.planFingerprint().equals(planFingerprint))
                throw new IllegalStateException(
                        "The idempotency key was already used for a different mutation plan.");
            switch (existing.state()) {
                case COMMITTED -> {
                    ExecutionReceipt original = existing.receipt();
                    var replay = new MutationExecutionResult(
                            new MutationBatchResult(existing.affectedRows().stream().map(MutationResult::new).toList()),
                            planFingerprint, original.resultFingerprint(), original.approvalId(),
                            original.approvedBy(), original);
                    return new LedgerBegin(replay, null);
                }
                case ABORTED -> {
                    var reopened = existing.reopen(requestId, correlationId, now, now.plus(leaseDuration), provisional);
                    var stored = ledger.tryUpdate(existing.version(), reopened);
                    if (stored.isPresent()) return new LedgerBegin(null, stored.get());
                }
                case STARTED -> {
                    if (existing.leaseExpiresAt() != null && now.isBefore(existing.leaseExpiresAt()))
                        throw new MutationInProgressException(existing.requestId(), key);
                    ledger.tryUpdate(existing.version(), existing.transition(MutationExecutionState.IN_DOUBT, now,
                            existing.receipt(), existing.affectedRows(), "lease expired without a recorded outcome"));
                    throw new MutationInDoubtException(existing.requestId(), key, null);
                }
                case IN_DOUBT -> throw new MutationInDoubtException(existing.requestId(), key, null);
            }
        }
        throw new MutationInProgressException(null, key);
    }

    private MutationExecutionRecord settle(MutationExecutionRecord record, MutationExecutionState next,
            ExecutionReceipt receipt, List<Integer> rows, String resolution) {
        var updated = record.transition(next, Instant.now(), receipt, rows, resolution);
        return ledger.tryUpdate(record.version(), updated).orElseThrow(() ->
                new MutationLedgerException("The execution record changed concurrently."));
    }

    /**
     * Records a commit the provider has confirmed. Recovery may have moved the record while the provider was running
     * (lease expiry), so a version conflict is re-read: the same attempt in STARTED or IN_DOUBT is committed, one that
     * recovery already committed is accepted. ABORTED or a different attempt means the provider's fencing guarantee was
     * broken, which is surfaced and never overwritten.
     */
    private MutationExecutionRecord settleCommitted(MutationExecutionRecord record, ExecutionReceipt receipt,
            List<Integer> rows) {
        MutationExecutionRecord current = record;
        for (int attempt = 0; attempt < 3; attempt++) {
            var stored = ledger.tryUpdate(current.version(),
                    current.transition(MutationExecutionState.COMMITTED, Instant.now(), receipt, rows, "executed"));
            if (stored.isPresent()) return stored.get();
            current = ledger.find(record.tenant(), record.subject(), record.idempotencyKey()).orElseThrow(() ->
                    new MutationLedgerException("The execution record disappeared."));
            if (!current.requestId().equals(record.requestId()) || current.state() == MutationExecutionState.ABORTED)
                throw new MutationLedgerException("The provider reported a commit for an attempt the ledger had already"
                        + " closed as not applied. Check the provider's reconciliation fencing.");
            if (current.state() == MutationExecutionState.COMMITTED) return current;
        }
        throw new MutationLedgerException("The execution record kept changing.");
    }

    /** Nothing was dispatched, so the intent is withdrawn and the key stays usable. Best effort. */
    private void abortUndispatched(MutationExecutionRecord active, String why) {
        if (active == null) return;
        try {
            settle(active, MutationExecutionState.ABORTED,
                    active.receipt().withOutcome("aborted", "aborted", Instant.now()), List.of(), why);
        } catch (RuntimeException ignored) {
            // the STARTED record expires into IN_DOUBT; recovery will find the provider has nothing
        }
    }

    /** Settles a failed dispatch: explicit "not applied" aborts; anything else leaves the outcome in doubt. */
    private RuntimeException settleFailure(MutationExecutionRecord active, RuntimeException failure) {
        if (active == null) return failure;
        boolean notApplied = failure instanceof MutationNotAppliedException;
        try {
            settle(active, notApplied ? MutationExecutionState.ABORTED : MutationExecutionState.IN_DOUBT,
                    active.receipt().withOutcome(notApplied ? "aborted" : "in_doubt", notApplied ? "aborted" : "in_doubt",
                            Instant.now()),
                    List.of(), notApplied ? "provider reported not applied" : "provider failed after dispatch");
        } catch (RuntimeException ledgerFailure) {
            failure.addSuppressed(ledgerFailure); // still STARTED; the lease turns it into IN_DOUBT
        }
        return notApplied ? failure : new MutationInDoubtException(active.requestId(), active.idempotencyKey(), failure);
    }

    private ExecutionReceipt buildReceipt(SemanticMutationPlan plan,
            com.foundgine.core.semantic.security.execution.SecurityExecutionContext security,
            MutationPlanApproval approval, String requestId, Instant startedAt, Instant completedAt,
            String resultFingerprint, int totalAffectedRows, String correlationId) {
        String planFingerprint = fingerprint(plan);
        String authorizationFingerprint = sha256Hex(
                policy.getClass().getName() + "|" + planFingerprint + "|"
                        + String.join(",", plan.requiredSecurityInvariants()) + "|"
                        + (security == null ? "anonymous-context" : security.authorityCachePartition()));
        String warrantId = security == null ? null : security.warrant().id();
        String warrantDigest = security == null ? null : security.warrant().digest();
        String invariantDigest = sha256Hex(String.join("|", plan.requiredSecurityInvariants()));
        ExecutionEvidence evidence = new ExecutionEvidence(
                provider.getClass().getName(),
                planFingerprint,
                plan.operations().stream()
                        // EntityId is an unsigned 64-bit hash; receipts carry 32-bit node ids, so keep the low 32 bits (never throw).
                        .map(operation -> (int) operation.entity().value())
                        .distinct()
                        .sorted()
                        .toList(),
                totalAffectedRows,
                Math.max(0, java.time.Duration.between(startedAt, completedAt).toMillis()),
                sha256Hex(provider.getClass().getName() + "|" + planFingerprint),
                sha256Hex("mutation-intent-v1|" + planFingerprint),
                authorizationFingerprint,
                warrantId,
                warrantDigest,
                invariantDigest,
                null);
        var versions = model == null
                ? new SemanticVersionSet("unversioned", 1, 1, 1, 1)
                : SemanticVersionSet.forModel(model);
        return ExecutionReceiptFactory.create(
                requestId,
                evidence,
                resultFingerprint,
                evidence.authorizedNodeIds(),
                describe(plan).effects(),
                startedAt,
                completedAt,
                versions.capabilityContractVersion(),
                versions.capabilityVersion(),
                versions.intentVersion(),
                versions.planVersion(),
                versions.semanticModelVersion(),
                approval != null ? approval.approvalId() : null,
                approval != null ? approval.approvedBy() : null,
                approval != null ? approval.approvedAt() : null,
                correlationId);
    }

    private static String normalizeCorrelationId(String value) {
        return value != null && value.matches("[A-Za-z0-9._:-]{1,128}")
                ? value : UUID.randomUUID().toString().replace("-", "");
    }

    private static String correlationId(ExecutionContext context) {
        Object value = context == null ? null : context.effectiveValues().get(ExecutionContextKeys.CORRELATION_ID);
        if (value instanceof String text && text.matches("[A-Za-z0-9._:-]{1,128}")) return text;
        return UUID.randomUUID().toString().replace("-", "");
    }

    private void recordDenied(SemanticMutationRequest request, RuntimeException failure) {
        recordDenied(request, failure, null);
    }

    private void recordDenied(SemanticMutationRequest request, RuntimeException failure, ExecutionContext context) {
        recordDenied(request, failure, context, correlationId(context));
    }

    private void recordDenied(SemanticMutationRequest request, RuntimeException failure,
            ExecutionContext context, String correlationId) {
        auditLog.record(AuditEvent.createCorrelated(
                AuditEvent.AuditCategory.MUTATION_DENIED, "semantic_mutation",
                request.security() == null ? null : request.security().subject(),
                request.security() == null ? null : request.security().tenant(),
                sha256Hex(failure.getClass().getName() + "|" + String.valueOf(failure.getMessage())),
                "Mutation rejected before execution: " + failure.getClass().getSimpleName(), correlationId));
    }

    private void validateWarrant(SemanticMutationRequest request) {
        var security = request.security();
        if (security == null) return;
        if (warrantKeyResolver == null)
            throw new IllegalStateException(
                    "A security warrant was supplied, but no warrant key resolver is configured.");

        Instant now = Instant.now();
        verifyWarrantTrust(security, now);
        validateWarrantRevocation(security, now);

        for (var operation : request.graph().operations()) {
            var capability =
                    securityContract.capabilities().stream()
                            .filter(
                                    c ->
                                            c.targetEntityId().equals(operation.entity())
                                                    && c.operation()
                                                            .equalsIgnoreCase(
                                                                    operation
                                                                            .kind()
                                                                            .name()
                                                                            .toLowerCase(
                                                                                    Locale.ROOT)))
                            .findFirst()
                            .orElseThrow(
                                    () ->
                                            new SecurityException(
                                                    "No security capability contract exists for"
                                                            + " entity '"
                                                            + operation.entity()
                                                            + "' and operation '"
                                                            + operation.kind()
                                                            + "'."));

            if (!SecurityWarrantAuthorization.allows(
                    security.warrant(),
                    security.subject(),
                    security.audience(),
                    capability.id(),
                    capability.operation(),
                    security.tenant(),
                    security.resourceScope()))
                throw new SecurityException(
                        "Security warrant does not authorize capability '"
                                + capability.id()
                                + "' for subject '"
                                + security.subject()
                                + ".");

            validateWarrantAllowedFields(operation, security);
        }
    }

    private void verifyWarrantTrust(
            com.foundgine.core.semantic.security.execution.SecurityExecutionContext security,
            Instant now) {
        SecurityWarrant warrant = security.warrant();
        boolean delegated = warrant.parentId() != null
                || warrant.parentDigest() != null
                || !warrant.delegationPath().isEmpty();
        if (delegated || security.delegationChain() != null) {
            if (expectedWarrantIssuer == null || expectedWarrantIssuer.isBlank())
                throw new IllegalStateException(
                        "Delegated warrant execution requires an explicitly configured root issuer.");
            SecurityWarrantExecutionTrust.verify(
                    warrant, warrantKeyResolver, expectedWarrantIssuer, security.audience(), now,
                    security.delegationChain(), warrantDelegationTrustResolver, security.tenant());
            return;
        }
        SecurityWarrantVerifier.verify(
                warrant, warrantKeyResolver, now, expectedWarrantIssuer, security.audience());
    }

    private SecurityWarrantRevocationSnapshot validateWarrantRevocation(
            com.foundgine.core.semantic.security.execution.SecurityExecutionContext security,
            Instant now) {
        if (warrantRevocationStore == null)
            throw new IllegalStateException(
                    "Warrant-backed mutation discovery and execution require a warrant revocation store; refusing to run without current revocation state.");
        return SecurityWarrantRevocationGuard.validate(
                security.warrant(), warrantRevocationStore, now);
    }

    private void validateWarrantAllowedFields(
            SemanticMutationOperation operation,
            com.foundgine.core.semantic.security.execution.SecurityExecutionContext security) {
        List<String> allowed = security.warrant().constraints().allowedFields();
        if (allowed.isEmpty()) return;
        if (model == null)
            throw new IllegalStateException(
                    "A mutation warrant specifies allowed fields, but no SemanticModel is"
                            + " configured to resolve them.");

        SemanticEntity entity = model.get(operation.entity());
        LinkedHashSet<String> requested = new LinkedHashSet<>();
        for (var field : operation.fields()) {
            if (field.source() != null) continue;
            entity.fields().stream()
                    .filter(x -> x.id().equals(field.field()))
                    .findFirst()
                    .ifPresent(x -> requested.add(x.name()));
        }
        for (var field : operation.returnFields())
            entity.fields().stream()
                    .filter(x -> x.id().equals(field))
                    .findFirst()
                    .ifPresent(x -> requested.add(x.name()));
        requested.addAll(filterFields(operation.filter(), entity));

        if (requested.stream().anyMatch(x -> !allowed.contains(x)))
            throw new SecurityException(
                    "Security warrant does not authorize one or more requested mutation fields on '"
                            + entity.name()
                            + "'.");
    }

    private static List<String> filterFields(
            SemanticFilterExpression filter, SemanticEntity entity) {
        if (filter == null) return List.of();
        List<String> result = new ArrayList<>();
        if (filter instanceof SemanticFieldFilter f) {
            entity.fields().stream()
                    .filter(x -> x.id().equals(f.field()))
                    .findFirst()
                    .ifPresent(x -> result.add(x.name()));
        } else if (filter instanceof SemanticAndFilter a) {
            for (var e : a.expressions()) result.addAll(filterFields(e, entity));
        } else if (filter instanceof SemanticOrFilter o) {
            for (var e : o.expressions()) result.addAll(filterFields(e, entity));
        }
        return result;
    }

    private void consumeWarrantReplay(
            com.foundgine.core.semantic.security.execution.SecurityExecutionContext security) {
        if (warrantReplayStore == null)
            throw new IllegalStateException(
                    "Executing a warrant-backed mutation requires a warrant replay store.");
        SecurityWarrantReplayGuard.consume(security.warrant(), warrantReplayStore, Instant.now());
    }

    private SemanticMutationPlan authorizeAndPlan(SemanticMutationRequest request) {
        MutationSecurityResourceLimitValidator.validate(request, securityResourceLimits);
        validateWarrant(request);

        SemanticMutationPlan semanticPlan = new SemanticMutationPlanner().plan(request.graph());
        SemanticMutationPlan authorizedPlan =
                new MutationAuthorizer(schema, policy).authorize(semanticPlan);
        List<String> required = requiredSecurityInvariantsFor(authorizedPlan);
        return new SemanticMutationPlan(
                authorizedPlan.operations(), authorizedPlan.dependencies(), required);
    }

    private List<String> requiredSecurityInvariantsFor(SemanticMutationPlan plan) {
        LinkedHashSet<String> required = new LinkedHashSet<>();
        for (var operation : plan.operations()) {
            var capability =
                    securityContract.capabilities().stream()
                            .filter(
                                    c ->
                                            c.targetEntityId().equals(operation.entity())
                                                    && c.operation()
                                                            .equalsIgnoreCase(
                                                                    operation
                                                                            .kind()
                                                                            .name()
                                                                            .toLowerCase(
                                                                                    Locale.ROOT)))
                            .findFirst()
                            .orElseThrow(
                                    () ->
                                            new IllegalStateException(
                                                    "No security capability contract exists for"
                                                            + " entity '"
                                                            + operation.entity()
                                                            + "' and operation '"
                                                            + operation.kind()
                                                            + "'."));
            required.addAll(capability.effectiveSecurityInvariants());
        }
        if (required.isEmpty())
            throw new IllegalStateException(
                    "A mutation execution cannot proceed without explicit security invariants.");
        return required.stream().sorted().toList();
    }

    private static boolean isEnginePreservedInvariant(String id) {
        return switch (id) {
            case SecurityInvariantIds.AUTHORIZATION_REQUIRED,
                    SecurityInvariantIds.RUNTIME_AUTHORIZATION,
                    SecurityInvariantIds.FIELD_VISIBILITY,
                    SecurityInvariantIds.RELATIONSHIP_VISIBILITY ->
                    true;
            default -> false;
        };
    }

    private MutationDryRunResult describe(SemanticMutationPlan plan) {
        List<MutationPlanOperation> operations = new ArrayList<>();
        for (int i = 0; i < plan.operations().size(); i++) {
            var x = plan.operations().get(i);
            var entity = schema.getEntity(x.entity());
            operations.add(
                    new MutationPlanOperation(
                            i,
                            entity.name(),
                            x.kind().name(),
                            x.fields().stream().map(f -> Long.toString(f.field().value())).toList(),
                            x.returnFields().stream().map(f -> Long.toString(f.value())).toList()));
        }
        List<String> effects =
                plan.operations().stream()
                        .flatMap(x -> x.effects().stream())
                        .map(this::formatEffect)
                        .toList();
        return new MutationDryRunResult(fingerprint(plan), operations, effects);
    }

    private String formatEffect(SemanticMutationEffect effect) {
        return effect.kind()
                + ":"
                + schema.getEntity(effect.entity()).name()
                + (effect.field() != null ? "." + effect.field().value() : "");
    }

    private static String fingerprint(SemanticMutationPlan plan) {
        try {
            // Insertion-ordered on purpose: Map.of iterates in a different order in every JVM, which would change the
            // fingerprint across restarts and replicas (breaking approvals and idempotent retries).
            Map<String, Object> document = new LinkedHashMap<>();
            document.put("version", "mutation-plan-v1");
            document.put("operations", plan.operations());
            document.put("dependencies", plan.dependencies());
            document.put("security", plan.requiredSecurityInvariants());
            return sha256Hex(CANONICAL.writeValueAsString(document));
        } catch (Exception e) {
            throw new IllegalStateException("Unable to fingerprint mutation plan.", e);
        }
    }

    private static String fingerprintResult(MutationBatchResult result) {
        try {
            List<Map<String, Object>> rows = new ArrayList<>();
            for (var x : result.results()) {
                Map<String, Object> row = new LinkedHashMap<>();
                row.put("affectedRows", x.affectedRows());
                if (x.returnedValues() != null) {
                    row.put(
                            "returned",
                            x.returnedValues().entrySet().stream()
                                    .sorted(
                                            Map.Entry.comparingByKey(
                                                    Comparator.comparingLong(FieldId::value)))
                                    .map(
                                            e -> {
                                                Map<String, Object> cell = new LinkedHashMap<>();
                                                cell.put("field", e.getKey().value());
                                                cell.put("value", e.getValue());
                                                return cell;
                                            })
                                    .toList());
                }
                rows.add(row);
            }
            return sha256Hex(CANONICAL.writeValueAsString(rows));
        } catch (Exception e) {
            throw new IllegalStateException("Unable to fingerprint mutation result.", e);
        }
    }

    private static String sha256Hex(String text) {
        try {
            byte[] digest =
                    MessageDigest.getInstance("SHA-256")
                            .digest(text.getBytes(StandardCharsets.UTF_8));
            StringBuilder out = new StringBuilder(digest.length * 2);
            for (byte b : digest) out.append(String.format("%02X", b));
            return out.toString();
        } catch (Exception e) {
            throw new IllegalStateException(e);
        }
    }
}
