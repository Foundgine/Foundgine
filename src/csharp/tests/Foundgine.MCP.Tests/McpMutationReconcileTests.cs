using System.Security.Cryptography;
using System.Text.Json;
using Foundgine.Core.Abstractions;
using Foundgine.Core.Execution;
using Foundgine.Core.Execution.Mutation;
using Foundgine.Core.Semantic;
using Foundgine.Core.Semantic.Authorization;
using Foundgine.Core.Semantic.Capabilities;
using Foundgine.Core.Semantic.Metadata;
using Foundgine.Core.Semantic.Security.Execution;
using Foundgine.Core.Semantic.Security.Warrants;
using Foundgine.Runtime;
using Foundgine.Runtime.Recovery;
using Xunit;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;

namespace Foundgine.Providers.Tools.MCP.Tests;

/// <summary>
/// An MCP execute whose response is lost can be reconciled with the same approval token, through the real tools,
/// a signed warrant, the mutation engine and an execution ledger.
/// </summary>
public sealed class McpMutationReconcileTests
{
    private const string Json =
        "{\"operations\":[{\"entity\":1,\"kind\":\"Update\",\"fields\":{\"2\":\"a\"}," +
        "\"filter\":{\"field\":1,\"operator\":\"Eq\",\"value\":1}}]}";

    [Theory]
    [InlineData(MutationVerdictOutcome.Applied, "committed")]
    [InlineData(MutationVerdictOutcome.NotApplied, "aborted")]
    [InlineData(MutationVerdictOutcome.Unknown, "unresolved")]
    public async Task Lost_response_is_reconciled_without_executing_again(MutationVerdictOutcome verdict, string status)
    {
        var fixture = new Fixture(verdict);
        var token = fixture.Tools(fixture.Security).Approve(Json, "ignored");
        await Assert.ThrowsAsync<MutationInDoubtException>(
            () => fixture.Tools(fixture.Security).ExecuteApprovedAsync(token));

        var output = await fixture.Tools(fixture.Security).ReconcileAsync(token);

        using var document = JsonDocument.Parse(output);
        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
        Assert.Equal(status == "committed", document.RootElement.TryGetProperty("receipt", out _));
        Assert.Equal(1, fixture.Provider.Calls);

        var claims = FoundgineMcpApprovalToken.DecodeAndVerify(token, fixture.Keys);
        Assert.NotNull(await fixture.Ledger.FindAsync("t1", "alice", "mcp-approval:" + claims.ApprovalId));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_execution()
    {
        var fixture = new Fixture(MutationVerdictOutcome.Applied);
        var token = fixture.Tools(fixture.Security).Approve(Json, "ignored");
        await Assert.ThrowsAsync<MutationInDoubtException>(
            () => fixture.Tools(fixture.Security).ExecuteApprovedAsync(token));

        var output = await fixture.Tools(fixture.Security with { Tenant = "t2" }).ReconcileAsync(token);

        using var document = JsonDocument.Parse(output);
        Assert.Equal("not_found", document.RootElement.GetProperty("status").GetString());
        Assert.False(document.RootElement.TryGetProperty("requestId", out _));
    }

    [Fact]
    public async Task Reconcile_requires_a_security_context()
    {
        var fixture = new Fixture(MutationVerdictOutcome.Applied);
        var token = fixture.Tools(fixture.Security).Approve(Json, "ignored");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Tools(null).ReconcileAsync(token));
    }

    private sealed class Fixture
    {
        private readonly RSA _tokenKey = RSA.Create(2048);
        private readonly FoundgineMutationEngine _engine;

        public Fixture(MutationVerdictOutcome verdict)
        {
            var model = new SemanticModelBuilder()
                .Entity(new EntityId(1), "Customer", c => c
                    .Identity(new FieldId(1), "Id")
                    .Field(new FieldId(2), "Name", typeof(string)))
                .Build();
            var policy = new AllowAllSemanticAuthorizationPolicy();
            var grants = SemanticCapabilityContractDiscovery.Describe(model, policy).Capabilities
                .Select(c => new CapabilityGrant(c.Id, c.Operation)).ToArray();
            var warrantKey = RSA.Create(2048);
            var warrant = SecurityWarrantSigner.Sign(new SecurityWarrant("w1", "issuer", "alice", "mcp", grants,
                SecurityWarrantConstraints.Unrestricted, DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(5), Guid.NewGuid().ToString("N"), "wk", null, []), warrantKey);
            Security = new SecurityExecutionContext(warrant, "alice", "mcp", "t1", null);
            Keys = new TokenKeys(_tokenKey);
            Provider = new LostResponseProvider(verdict);

            var columns = new Dictionary<FieldId, ColumnId?>
            {
                [new FieldId(1)] = new ColumnId(11),
                [new FieldId(2)] = new ColumnId(12)
            };
            var entity = new MutationEntitySchema(new EntityId(1), "Customer",
                columns.Values.Select(x => x!.Value).ToHashSet(), columns, new ColumnId(11));
            _engine = new FoundgineMutationEngine(new Schema(entity), policy, Provider, model,
                new WarrantKeys(warrantKey), "issuer", new MemorySecurityWarrantReplayStore(), null,
                new MemorySecurityWarrantRevocationStore(), ledger: Ledger);
        }

        public SecurityExecutionContext Security { get; }
        public IFoundgineMcpApprovalTokenKeyResolver Keys { get; }
        public LostResponseProvider Provider { get; }
        public InMemoryMutationExecutionLedger Ledger { get; } = new();

        public FoundgineMcpMutationTools Tools(SecurityExecutionContext? who) => new(
            _engine, () => who,
            approvalTokenSigner: new FoundgineMcpRsaApprovalTokenSigner("token-key", _tokenKey),
            approvalTokenKeys: Keys,
            approvalTokenPolicy: new FoundgineMcpApprovalTokenPolicy(new ReplayStore()));
    }

    private sealed class TokenKeys(RSA key) : IFoundgineMcpApprovalTokenKeyResolver
    {
        public RSA? Resolve(string keyId) => keyId == "token-key" ? key : null;
    }

    private sealed class WarrantKeys(RSA key) : ISecurityWarrantKeyResolver
    {
        public RSA Resolve(string keyId) => key;
    }

    private sealed class ReplayStore : IFoundgineMcpApprovalTokenReplayStore
    {
        private readonly HashSet<string> _used = [];
        public bool TryConsume(string approvalId, long expiresAtUnixMilliseconds) => _used.Add(approvalId);
    }

    private sealed class Schema(MutationEntitySchema entity) : IMutationSchema
    {
        public MutationEntitySchema GetEntity(EntityId entityId) => entity;

        public MutationRelationshipSchema GetRelationship(RelationshipId relationshipId) =>
            throw new KeyNotFoundException();
    }

    private sealed class LostResponseProvider(MutationVerdictOutcome verdict) :
        IMutationBatchExecutionProvider, IMutationSecurityConformanceEvaluator, IMutationReconciler
    {
        public int Calls { get; private set; }

        public MutationBatchResult ExecuteBatch(ExecutionMutationIR ir, ExecutionContext context)
        {
            Calls++;
            throw new IOException("response lost");
        }

        public MutationSecurityConformanceResult Evaluate(ExecutionMutationIR ir) =>
            new(GetType().FullName!, ir.RequiredSecurityInvariants, []);

        public ValueTask<MutationReconciliationVerdict> ReconcileAsync(
            MutationReconciliationQuery query, CancellationToken cancellationToken = default) =>
            new(new MutationReconciliationVerdict(
                verdict, verdict == MutationVerdictOutcome.Applied ? "evidence" : null, null));
    }
}
