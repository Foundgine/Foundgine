using System.Security.Claims;
using Foundgine.Core.Abstractions;
using Foundgine.Core.Semantic.Mutation;
using Foundgine.Runtime;
using Xunit;

namespace Foundgine.SupplyChain.Advanced.Tests;

/// <summary>
/// Lifecycle payload rules of the protocol-neutral mutation command. Mirrors the Java
/// <c>FoundgineIntentBoundaryContractTest.mutationIntentFactoriesEnforceLifecyclePayloads</c>.
/// </summary>
public sealed class FoundgineMutationIntentContractTests
{
    private static SemanticMutationRequest Mutation() =>
        new(new SemanticMutationOperationGraph(
        [
            new SemanticMutationOperation(
                EntityId.Create("Command"),
                SemanticMutationKind.Create,
                Fields: [],
                Filter: null,
                ConflictFields: [],
                ReturnFields: [],
                Effects: [],
                Dependencies: [])
        ]));

    [Fact]
    public void FactoriesEnforceLifecyclePayloads()
    {
        var mutation = Mutation();
        Assert.Equal(FoundgineMutationIntentOperation.DryRun, FoundgineMutationIntentRequest.DryRun(mutation).Operation);
        Assert.Same(mutation, FoundgineMutationIntentRequest.Execute(mutation).Mutation);
        Assert.Equal(FoundgineMutationIntentOperation.Approve, FoundgineMutationIntentRequest.Approve(mutation).Operation);

        var approval = new MutationPlanApproval(
            mutation, "approval", "fingerprint", "trusted-caller", DateTimeOffset.UtcNow);
        Assert.Same(approval, FoundgineMutationIntentRequest.ExecuteApproved(approval).Approval);
        Assert.Equal(
            "trace-002",
            FoundgineMutationIntentRequest.Execute(mutation).WithCorrelationId("trace-002").CorrelationId);

        Assert.ThrowsAny<ArgumentException>(() =>
            new FoundgineMutationIntentRequest(FoundgineMutationIntentOperation.Approve, null, null));
        Assert.ThrowsAny<ArgumentException>(() =>
            new FoundgineMutationIntentRequest(FoundgineMutationIntentOperation.ExecuteApproved, mutation, null));
        Assert.ThrowsAny<ArgumentException>(() =>
            new FoundgineMutationIntentRequest(FoundgineMutationIntentOperation.Execute, mutation, approval));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void UnsafeCorrelationIdsAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => FoundgineMutationIntentRequest.Execute(Mutation()).WithCorrelationId(value));
        Assert.Throws<ArgumentException>(() => new FoundgineIntentRequest(FoundgineIntentOperation.Capabilities, null, value));
        Assert.Throws<ArgumentException>(() =>
            new FoundgineIntentRequest(FoundgineIntentOperation.Capabilities, null, new string('a', 129)));
    }

    [Fact]
    public async Task HandlerFailsClosedWhenAnApplicationDoesNotEnableWrites()
    {
        IFoundgineIntentHandler handler = new ReadOnlyHandler();
        var authenticated = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "trusted-caller")], "test"));

        var forbidden = await handler.HandleMutationAsync(FoundgineMutationIntentRequest.Execute(Mutation()), authenticated);
        Assert.Equal(FoundgineIntentOutcome.Forbidden, forbidden.Outcome);
        Assert.Equal("mutation_not_supported", forbidden.ErrorCode);

        var anonymous = await handler.HandleMutationAsync(
            FoundgineMutationIntentRequest.Execute(Mutation()), new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.Equal(FoundgineIntentOutcome.Unauthenticated, anonymous.Outcome);
    }

    private sealed class ReadOnlyHandler : IFoundgineIntentHandler
    {
        public Task<FoundgineIntentResult> HandleAsync(
            FoundgineIntentRequest request, ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
            Task.FromResult(FoundgineIntentResult.Success("read"));
    }
}
