package com.foundgine.providers.tools.mcp;

import static org.junit.jupiter.api.Assertions.*;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.foundgine.core.abstractions.*;
import com.foundgine.core.execution.ExecutionContext;
import com.foundgine.core.execution.ExecutionContextKeys;
import com.foundgine.core.execution.mutation.*;
import com.foundgine.core.semantic.*;
import com.foundgine.core.semantic.authorization.AllowAllSemanticAuthorizationPolicy;
import com.foundgine.core.semantic.capabilities.SemanticCapabilityContractDiscovery;
import com.foundgine.core.semantic.security.execution.SecurityExecutionContext;
import com.foundgine.core.semantic.security.warrants.*;
import com.foundgine.runtime.FoundgineMutationEngine;
import com.foundgine.runtime.controlplane.auditlog.InMemoryAuditLog;
import com.foundgine.runtime.recovery.*;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.interfaces.RSAPublicKey;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;

/**
 * The lost-response scenario through the MCP tools: the approval token is spent before the engine runs, the provider
 * commits, the response never reaches the agent. {@code foundgine_mutation_reconcile} must report the real outcome, and
 * a retry must neither re-execute nor leave the agent guessing.
 */
class McpReconcileRoundTripTest {
    private static final ObjectMapper M = new ObjectMapper();
    private static final EntityId CUSTOMER = EntityId.create("Customer");
    private static final FieldId ID = FieldId.create("Customer", "Id");
    private static final FieldId NAME = FieldId.create("Customer", "Name");

    private static String mutation(String name) {
        return "{\"operations\":[{\"entity\":" + Long.toUnsignedString(CUSTOMER.value()) + ",\"kind\":\"create\",\"fields\":{\""
                + Long.toUnsignedString(NAME.value()) + "\":\"" + name + "\"},\"returnFields\":["
                + Long.toUnsignedString(NAME.value()) + "]}]}";
    }

    static final class Provider implements IMutationBatchExecutionProvider, IMutationSecurityConformanceEvaluator, IMutationReconciler {
        final AtomicInteger calls = new AtomicInteger();
        final Set<String> committed = new HashSet<>();
        volatile boolean loseAcknowledgement;

        @Override public MutationBatchResult executeBatch(ExecutionMutationIR ir, ExecutionContext context) {
            calls.incrementAndGet();
            committed.add((String) context.effectiveValues().get(ExecutionContextKeys.EXECUTION_REQUEST_ID));
            if (loseAcknowledgement) throw new IllegalStateException("connection reset after commit");
            return new MutationBatchResult(List.of(new MutationResult(1)));
        }
        @Override public MutationSecurityConformanceResult evaluate(ExecutionMutationIR ir) {
            return new MutationSecurityConformanceResult(getClass().getName(), ir.requiredSecurityInvariants(), List.of());
        }
        @Override public MutationReconciliationVerdict reconcile(MutationReconciliationQuery q) {
            return committed.contains(q.requestId())
                    ? MutationReconciliationVerdict.applied("marker:" + q.requestId())
                    : MutationReconciliationVerdict.notApplied("no marker");
        }
    }

    private static final class Fixture {
        final Provider provider = new Provider();
        final FoundgineMcpMutationTools tools;
        final FoundgineMcpMutationTools otherCaller;
        final KeyPair approvalKey;
        final FoundgineMcpApprovalToken.Signer signer;

        Fixture(boolean withLedger) throws Exception {
            var model = new SemanticModelBuilder()
                    .entity(CUSTOMER, "Customer", e -> e.identity(ID, "Id").field(NAME, "Name", String.class)).build();
            var columns = new LinkedHashMap<FieldId, ColumnId>();
            columns.put(ID, new ColumnId(1));
            columns.put(NAME, new ColumnId(2));
            var entitySchema = new MutationEntitySchema(CUSTOMER, "Customer", new LinkedHashSet<>(columns.values()), columns, new ColumnId(1));
            MutationSchema schema = new MutationSchema() {
                @Override public MutationEntitySchema getEntity(EntityId id) {
                    if (CUSTOMER.equals(id)) return entitySchema;
                    throw new NoSuchElementException();
                }
                @Override public MutationRelationshipSchema getRelationship(RelationshipId id) { throw new NoSuchElementException(); }
            };
            var policy = new AllowAllSemanticAuthorizationPolicy();
            var gen = KeyPairGenerator.getInstance("RSA");
            gen.initialize(2048);
            KeyPair key = gen.generateKeyPair();
            ISecurityWarrantKeyResolver resolver = keyId -> (RSAPublicKey) key.getPublic();
            var engine = new FoundgineMutationEngine(schema, policy, provider, model, resolver, "issuer",
                    new MemorySecurityWarrantReplayStore(), null, new MemorySecurityWarrantRevocationStore(), null,
                    new InMemoryAuditLog(), withLedger ? new InMemoryMutationExecutionLedger() : null, null, null);

            var grants = SemanticCapabilityContractDiscovery.describe(model, policy).capabilities().stream()
                    .filter(c -> c.targetEntityId().equals(CUSTOMER) && c.operation().equalsIgnoreCase("create"))
                    .map(c -> new CapabilityGrant(c.id(), c.operation(), List.of())).toList();
            var ctx = context(key, "alice", "w-1", "nonce-1", grants);
            var otherCtx = context(key, "mallory", "w-2", "nonce-2", grants);
            var tokenKeys = KeyPairGenerator.getInstance("RSA");
            tokenKeys.initialize(2048);
            approvalKey = tokenKeys.generateKeyPair();
            signer = FoundgineMcpApprovalToken.rsaSha256Pkcs1Signer("mcp-approval-1", approvalKey.getPrivate());
            FoundgineMcpApprovalToken.KeyResolver keys = keyId -> keyId.equals("mcp-approval-1") ? approvalKey.getPublic() : null;
            var used = java.util.concurrent.ConcurrentHashMap.<String>newKeySet();
            var policyStore = FoundgineMcpApprovalTokenPolicy.defaults(new FoundgineMcpApprovalTokenPolicy.ReplayStore() {
                public boolean tryConsume(String id, long expiresAt) { return used.add(id); }
            });
            tools = new FoundgineMcpMutationTools(engine, () -> ctx, null, signer, keys, policyStore);
            otherCaller = new FoundgineMcpMutationTools(engine, () -> otherCtx, null, signer, keys, policyStore);
        }

        private static SecurityExecutionContext context(KeyPair key, String subject, String id, String nonce, List<CapabilityGrant> grants) throws Exception {
            var now = Instant.now();
            var warrant = SecurityWarrantSigner.sign(
                    new SecurityWarrant(id, "issuer", subject, "api", grants,
                            new SecurityWarrantConstraints(List.of("tenant-a"), null, null, null, null, null),
                            now.minusSeconds(60), now.plusSeconds(600), nonce, "key-1", null, new byte[0], null, List.of()),
                    key.getPrivate());
            return new SecurityExecutionContext(warrant, subject, "api", "tenant-a", "tenant-a");
        }
    }

    private static Throwable failure(java.util.concurrent.CompletionStage<String> stage) {
        var e = assertThrows(ExecutionException.class, () -> stage.toCompletableFuture().get());
        return e.getCause();
    }

    @Test
    void lostResponseIsRecoveredByReconcileAndRetryNeverReExecutes() throws Exception {
        var f = new Fixture(true);
        var token = f.tools.approve(mutation("Ada"), "x");
        f.provider.loseAcknowledgement = true;

        assertInstanceOf(MutationInDoubtException.class, failure(f.tools.executeApprovedAsync(token)));
        // the token is spent: presenting it again is refused, which is why the agent needs a way to ask
        assertNotNull(failure(f.tools.executeApprovedAsync(token)));
        assertEquals(1, f.provider.calls.get());

        var report = M.readTree(f.tools.reconcile(token));
        assertEquals("committed", report.get("status").asText(), report.toString());
        assertEquals("reconciled", report.at("/receipt/status").asText());
        assertTrue(report.get("nextStep").asText().contains("Do not execute it again"));
        assertEquals(1, f.provider.calls.get());

        var again = M.readTree(f.tools.reconcile(token));
        assertEquals("committed", again.get("status").asText());
        assertFalse(again.get("detail").asText().isEmpty());
    }

    @Test
    void anApprovalThatNeverReachedTheEngineReportsNotFound() throws Exception {
        var f = new Fixture(true);
        var token = f.tools.approve(mutation("Ada"), "x");
        var report = M.readTree(f.tools.reconcile(token));
        assertEquals("not_found", report.get("status").asText());
        assertTrue(report.get("nextStep").asText().contains("does not prove"), report.toString());
        assertEquals(0, f.provider.calls.get());
    }

    @Test
    void successfulExecutionReconcilesAsCommittedWithItsReceipt() throws Exception {
        var f = new Fixture(true);
        var token = f.tools.approve(mutation("Ada"), "x");
        var exec = M.readTree(f.tools.executeApprovedAsync(token).toCompletableFuture().get());
        var report = M.readTree(f.tools.reconcile(token));
        assertEquals("committed", report.get("status").asText());
        assertEquals(exec.at("/receipt/requestId").asText(), report.at("/receipt/requestId").asText());
    }

    @Test
    void anotherCallerCannotSeeSomeoneElsesExecution() throws Exception {
        var f = new Fixture(true);
        var token = f.tools.approve(mutation("Ada"), "x");
        f.tools.executeApprovedAsync(token).toCompletableFuture().get();
        var report = M.readTree(f.otherCaller.reconcile(token));
        assertEquals("not_found", report.get("status").asText());
    }

    @Test
    void tamperedTokenIsRefused() throws Exception {
        var f = new Fixture(true);
        var token = f.tools.approve(mutation("Ada"), "x");
        var broken = token.substring(0, token.length() - 4) + "AAAA";
        assertThrows(RuntimeException.class, () -> f.tools.reconcile(broken));
    }

    @Test
    void reconcileToolIsOnlyListedWhenALedgerIsConfigured() throws Exception {
        for (boolean ledger : new boolean[] {false, true}) {
            var f = new Fixture(ledger);
            var server = FoundgineMcpServer.builder().withMutationTools(f.tools).build();
            var list = M.readTree(server.handleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}")
                    .toCompletableFuture().get().orElseThrow()).at("/result/tools");
            var names = new ArrayList<String>();
            list.forEach(t -> names.add(t.get("name").asText()));
            assertEquals(ledger, names.contains("foundgine_mutation_reconcile"), names.toString());
            assertEquals(ledger, f.tools.reconcileAvailable());
        }
    }

    @Test
    void reconcileThroughTheServerReportsTheOutcome() throws Exception {
        var f = new Fixture(true);
        var server = FoundgineMcpServer.builder().withMutationTools(f.tools).build();
        var token = f.tools.approve(mutation("Ada"), "x");
        f.provider.loseAcknowledgement = true;
        failure(f.tools.executeApprovedAsync(token));
        var body = M.writeValueAsString(M.createObjectNode().put("jsonrpc", "2.0").put("id", 7).put("method", "tools/call")
                .set("params", M.createObjectNode().put("name", "foundgine_mutation_reconcile")
                        .set("arguments", M.createObjectNode().put("approvalJson", token))));
        var out = M.readTree(server.handleAsync(body).toCompletableFuture().get().orElseThrow());
        assertFalse(out.at("/result/isError").asBoolean(), out.toString());
        assertEquals("committed", M.readTree(out.at("/result/content/0/text").asText()).get("status").asText());
    }
}
