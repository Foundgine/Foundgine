package com.foundgine.providers.tools.mcp;

import static com.foundgine.core.semantic.security.execution.SecurityExecutionContextProviderExtensions.requireSecurityExecutionContext;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.foundgine.core.execution.CancellationToken;
import com.foundgine.core.semantic.security.execution.DelegateSecurityExecutionContextProvider;
import com.foundgine.core.semantic.security.execution.ISecurityExecutionContextProvider;
import com.foundgine.core.semantic.security.execution.SecurityExecutionContext;
import com.foundgine.runtime.IFoundgineMutations;
import com.foundgine.runtime.MutationPlanApproval;
import com.foundgine.runtime.SemanticMutationRequest;
import java.time.Instant;
import java.util.Objects;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.function.Supplier;

/**
 * Mutation tools on the Foundgine mutation boundary (port of {@code FoundgineMcpMutationTools}): dry-run, approve and
 * execute-approved. Nothing executes without an approval bound to the exact authorized plan, execution re-authorizes against the
 * host's context, and the caller can never supply identity or tenant: the host context replaces whatever a request carried.
 *
 * <p><b>Approval token.</b> {@link #approve} returns a versioned, RSA-SHA256 signed {@code fgmcp1} token. Both signing and
 * verification services are supplied by the host. Execution verifies the signature before parsing claims, binds the current
 * host security context, and lets the engine re-authorize and verify the exact plan fingerprint. Unsigned legacy JSON approvals
 * are not accepted. Not exposing these tools to read-only agents remains a host registration decision.
 */
public final class FoundgineMcpMutationTools {
    /** Legacy pass-through executor. Prefer the {@link IFoundgineMutations} constructor. */
    @Deprecated
    public interface Executor {
        CompletionStage<Object> execute(String mutationJson);
    }

    private final IFoundgineMutations mutations;
    private final ISecurityExecutionContextProvider securityContextProvider;
    private final Executor legacy;
    private final FoundgineMcpApprovalToken.Signer approvalTokenSigner;
    private final FoundgineMcpApprovalToken.KeyResolver approvalTokenKeys;
    private final FoundgineMcpApprovalTokenPolicy approvalTokenPolicy;

    /**
     * @param mutations the mutation engine; may be {@code null}, in which case every tool reports it is not configured
     * @param securityContextFactory delegate form of the provider; both may not be supplied together
     * @param securityContextProvider host-owned source of the caller's context
     */
    public FoundgineMcpMutationTools(
            IFoundgineMutations mutations,
            Supplier<SecurityExecutionContext> securityContextFactory,
            ISecurityExecutionContextProvider securityContextProvider) {
        this(mutations, securityContextFactory, securityContextProvider, null, null, null);
    }

    /** Configures portable signed approvals. Both signing and verification services are host-owned. */
    public FoundgineMcpMutationTools(
            IFoundgineMutations mutations,
            Supplier<SecurityExecutionContext> securityContextFactory,
            ISecurityExecutionContextProvider securityContextProvider,
            FoundgineMcpApprovalToken.Signer approvalTokenSigner,
            FoundgineMcpApprovalToken.KeyResolver approvalTokenKeys) {
        this(mutations, securityContextFactory, securityContextProvider, approvalTokenSigner, approvalTokenKeys, null);
    }

    /** Configures signing, verification and mandatory expiry/single-use enforcement. */
    public FoundgineMcpMutationTools(
            IFoundgineMutations mutations, Supplier<SecurityExecutionContext> securityContextFactory,
            ISecurityExecutionContextProvider securityContextProvider,
            FoundgineMcpApprovalToken.Signer approvalTokenSigner,
            FoundgineMcpApprovalToken.KeyResolver approvalTokenKeys,
            FoundgineMcpApprovalTokenPolicy approvalTokenPolicy) {
        if (securityContextProvider != null && securityContextFactory != null)
            throw new IllegalArgumentException(
                    "Only one of securityContextProvider or securityContextFactory may be supplied.");
        this.mutations = mutations;
        this.securityContextProvider =
                securityContextProvider != null
                        ? securityContextProvider
                        : new DelegateSecurityExecutionContextProvider(
                                securityContextFactory != null ? securityContextFactory : () -> null);
        this.legacy = null;
        if ((approvalTokenSigner == null) != (approvalTokenKeys == null))
            throw new IllegalArgumentException("Both approval-token signer and key resolver must be supplied together.");
        this.approvalTokenSigner = approvalTokenSigner;
        this.approvalTokenKeys = approvalTokenKeys;
        this.approvalTokenPolicy = approvalTokenPolicy;
    }

    /** @deprecated pass-through without the approval boundary; kept for existing hosts and samples. */
    @Deprecated
    public FoundgineMcpMutationTools(Executor executor) {
        this.legacy = Objects.requireNonNull(executor);
        this.mutations = null;
        this.securityContextProvider = null;
        this.approvalTokenSigner = null;
        this.approvalTokenKeys = null;
        this.approvalTokenPolicy = null;
    }

    /** Validates and authorizes a mutation without executing it; returns the plan fingerprint and declared effects. */
    public String dryRun(String mutationJson) {
        var engine = requireMutations();
        var request = withHostSecurity(MutationJsonAdapter.parse(mutationJson));
        return write(engine.dryRun(request));
    }

    /**
     * Creates an approval bound to the exact authorized plan. The approver is the host-authenticated subject;
     * {@code approvedBy} from the agent is deliberately ignored.
     */
    public String approve(String mutationJson, String approvedBy) {
        var engine = requireMutations();
        var request = withHostSecurity(MutationJsonAdapter.parse(mutationJson));
        var security = request.security();
        if (security == null)
            throw new SecurityException("MCP mutation approval requires a host-supplied SecurityExecutionContext.");
        if (approvalTokenSigner == null)
            throw new IllegalStateException("MCP approval-token signing is not configured; unsigned approvals are disabled.");
        var approval = engine.approve(request, security.subject());
        try {
            return FoundgineMcpApprovalToken.encode(
                    approval.approvalId(), approval.planFingerprint(), approval.approvedBy(),
                    approval.approvedAt().toEpochMilli(), mutationJson, approvalTokenSigner);
        } catch (java.security.GeneralSecurityException e) {
            throw new IllegalStateException("Unable to sign the mutation approval token.", e);
        }
    }

    public CompletionStage<String> executeApprovedAsync(String approvalJson) {
        return executeApprovedAsync(approvalJson, CancellationToken.NONE);
    }

    /** Executes only an approved mutation, after re-authorization and exact plan fingerprint verification. */
    public CompletionStage<String> executeApprovedAsync(String approvalJson, CancellationToken cancellationToken) {
        try {
            if (approvalTokenKeys == null)
                throw new IllegalStateException("MCP approval-token verification is not configured; unsigned approvals are disabled.");
            var claims = FoundgineMcpApprovalToken.decodeAndVerify(approvalJson, approvalTokenKeys);
            var request = MutationJsonAdapter.parse(claims.requestJson());
            var security = requireSecurityExecutionContext(securityContextProvider, "MCP", "mutation execution");
            var engine = requireMutations();
            if (approvalTokenPolicy == null)
                throw new SecurityException("MCP approval-token expiry and replay policy is not configured; execution is disabled.");
            approvalTokenPolicy.validateAndConsume(claims);
            var bound = new MutationPlanApproval(
                    new SemanticMutationRequest(request.graph(), security),
                    claims.approvalId(), claims.planFingerprint(), claims.approvedBy(),
                    Instant.ofEpochMilli(claims.approvedAtUnixMilliseconds()));
            // The single-use approval id is the idempotency key: it lets the engine's execution ledger answer
            // foundgine_mutation_reconcile even though the token itself can never be presented again.
            var context = new com.foundgine.core.execution.ExecutionContext(java.util.Map.of(
                    com.foundgine.core.execution.ExecutionContextKeys.IDEMPOTENCY_KEY,
                    idempotencyKeyFor(claims.approvalId())));
            return engine.executeApprovedAsync(bound, context, cancellationToken)
                    .thenApply(FoundgineMcpMutationTools::write);
        } catch (java.security.GeneralSecurityException e) {
            return CompletableFuture.failedFuture(new SecurityException("MCP approval token verification failed.", e));
        } catch (RuntimeException e) {
            return CompletableFuture.failedFuture(e);
        }
    }

    /** True when the engine keeps an execution ledger, so {@link #reconcile} can answer. */
    public boolean reconcileAvailable() {
        if (mutations == null) return false;
        try {
            var recovery = mutations.recovery();
            return recovery != null && recovery.isPresent();
        } catch (UnsupportedOperationException notSupported) { // proxies and partial decorators
            return false;
        }
    }

    /**
     * Reports what became of an approval that was presented to {@code executeApprovedAsync}, for the case where the
     * agent never saw the response. The token's signature is verified but it is <em>not</em> consumed (it is already
     * spent), and only the caller's own tenant/subject scope is searched. It never executes anything.
     */
    public String reconcile(String approvalJson) {
        try {
            if (approvalTokenKeys == null)
                throw new IllegalStateException("MCP approval-token verification is not configured.");
            var claims = FoundgineMcpApprovalToken.decodeAndVerify(approvalJson, approvalTokenKeys);
            var security = requireSecurityExecutionContext(securityContextProvider, "MCP", "mutation reconciliation");
            var recovery = requireMutations().recovery().orElseThrow(() -> new FoundgineMcpNotConfiguredException(
                    "Mutation reconciliation is not configured. Configure FoundgineOptions.mutationLedger."));
            var report = recovery.reconcile(security.tenant(), security.subject(), idempotencyKeyFor(claims.approvalId()));
            var out = new java.util.LinkedHashMap<String, Object>();
            out.put("status", report.outcome().name().toLowerCase(java.util.Locale.ROOT));
            out.put("detail", report.detail());
            out.put("nextStep", nextStep(report.outcome()));
            if (report.record() != null) {
                out.put("state", report.record().state().name().toLowerCase(java.util.Locale.ROOT));
                out.put("attempt", report.record().attempt());
                out.put("requestId", report.record().requestId());
                if (report.outcome() == com.foundgine.runtime.recovery.MutationReconciliationReport.Outcome.COMMITTED)
                    out.put("receipt", report.record().receipt());
            }
            return write(out);
        } catch (java.security.GeneralSecurityException e) {
            throw new SecurityException("MCP approval token verification failed.", e);
        }
    }

    private static String nextStep(com.foundgine.runtime.recovery.MutationReconciliationReport.Outcome outcome) {
        return switch (outcome) {
            case COMMITTED -> "The mutation was applied. Do not execute it again; use the receipt as the result.";
            case ABORTED -> "That attempt was not applied. Call foundgine_mutation_approve for a new approval and execute again if still wanted.";
            case IN_PROGRESS -> "The execution is still running. Call this tool again shortly; do not execute again.";
            case UNRESOLVED -> "The outcome could not be determined. Do not execute again; ask an operator to resolve it.";
            case NOT_FOUND -> "No execution is recorded for this approval under your identity. This does not prove that nothing "
                    + "was applied: the original call may not have reached the ledger yet, or may have run under another "
                    + "identity. Do not execute again until the original call has ended; call this tool again, and if it still "
                    + "reports not_found, check the target data before asking for a new approval.";
        };
    }

    /** Idempotency key for an approval: the id itself when it is a safe identifier, otherwise its SHA-256. */
    static String idempotencyKeyFor(String approvalId) {
        if (approvalId != null && ("mcp-approval:" + approvalId).matches("[A-Za-z0-9._:-]{1,128}"))
            return "mcp-approval:" + approvalId;
        try {
            var d = java.security.MessageDigest.getInstance("SHA-256")
                    .digest(String.valueOf(approvalId).getBytes(java.nio.charset.StandardCharsets.UTF_8));
            return "mcp-approval:" + java.util.HexFormat.of().formatHex(d);
        } catch (java.security.NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    /** @deprecated legacy pass-through; use dryRun / approve / executeApprovedAsync. */
    @Deprecated
    public CompletionStage<Object> foundgineMutation(String json) {
        if (legacy == null) throw new FoundgineMcpNotConfiguredException("Legacy mutation execution is not configured.");
        return legacy.execute(json);
    }

    private SemanticMutationRequest withHostSecurity(SemanticMutationRequest request) {
        return new SemanticMutationRequest(request.graph(),
                requireSecurityExecutionContext(securityContextProvider, "MCP", "mutation execution"));
    }

    private IFoundgineMutations requireMutations() {
        if (mutations == null)
            throw new FoundgineMcpNotConfiguredException(
                    "Foundgine mutation execution is not configured. Configure FoundgineOptions.MutationSchema and MutationProvider.");
        return mutations;
    }

    private static String write(Object value) {
        try {
            return FoundgineMcpJson.WRITER.writeValueAsString(value);
        } catch (JsonProcessingException e) {
            throw new IllegalStateException("Unable to serialize the MCP tool result.", e);
        }
    }
}
