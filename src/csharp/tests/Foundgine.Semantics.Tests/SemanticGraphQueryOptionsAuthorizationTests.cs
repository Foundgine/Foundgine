using Foundgine.Core.Abstractions;
using Foundgine.Core.Semantic;
using Foundgine.Core.Semantic.Authorization;
using Foundgine.Core.Semantic.Metadata;
using Foundgine.Core.Semantic.Query;
using Foundgine.Core.Semantic.Resolution;
using Xunit;

namespace Foundgine.Core.Semantic.Tests;

/// <summary>
/// Filters and ordering observe field values, so the contract-aware graph overload must refuse them when the
/// caller cannot read the field. The contract-less <c>Authorize(SemanticGraph)</c> overload does not check
/// query options; these tests pin the behaviour of the overload that does.
/// </summary>
public sealed class SemanticGraphQueryOptionsAuthorizationTests
{
    private static readonly EntityId Customer = new(1);
    private static readonly EntityId Account = new(2);
    private static readonly FieldId Id = new(1);
    private static readonly FieldId Name = new(2);
    private static readonly FieldId Balance = new(3);
    private static readonly RelationshipId Accounts = new(1);

    private static SemanticContractSnapshot Contract() =>
        new SemanticModelBuilder()
            .Entity(Customer, "Customer", e => e
                .Identity(Id, "Id")
                .Field(Name, "Name", typeof(string))
                .Relationship(Accounts, "Accounts", Account, RelationshipCardinality.Many))
            .Entity(Account, "Account", e => e
                .Identity(Id, "Id")
                .Field(Balance, "Balance", typeof(decimal)))
            .Build()
            .Freeze()
            .CreateSnapshot();

    private static SemanticGraph Resolve(SemanticContractSnapshot contract, SemanticQueryOptions? options) =>
        new SemanticRequestResolver(contract).Resolve(
            new SemanticRequest(Customer, [new SemanticSelection(Id, null, [])], options));

    [Fact]
    public void Filter_on_unreadable_root_field_is_refused()
    {
        var contract = Contract();
        var graph = Resolve(contract, new SemanticQueryOptions(
            Filter: new SemanticFieldFilter(Name, SemanticFilterOperator.Eq, "x")));

        Assert.Throws<SemanticAuthorizationException>(() =>
            new SemanticAuthorizer(new DenyCustomerNamePolicy()).Authorize(contract, graph));
    }

    [Fact]
    public void Filter_through_relationship_on_unreadable_field_is_refused()
    {
        var contract = Contract();
        var graph = Resolve(contract, new SemanticQueryOptions(
            Filter: new SemanticRelationshipFilter(
                Accounts,
                SemanticRelationshipQuantifier.Some,
                new SemanticFieldFilter(Balance, SemanticFilterOperator.Eq, 0m))));

        Assert.Throws<SemanticAuthorizationException>(() =>
            new SemanticAuthorizer(new DenyBalancePolicy()).Authorize(contract, graph));
    }

    [Fact]
    public void Order_by_unreadable_field_is_refused()
    {
        var contract = Contract();
        var graph = Resolve(contract, new SemanticQueryOptions(
            Order: [new SemanticOrderTerm(Name, SemanticSortDirection.Asc)]));

        Assert.Throws<SemanticAuthorizationException>(() =>
            new SemanticAuthorizer(new DenyCustomerNamePolicy()).Authorize(contract, graph));
    }

    [Fact]
    public void Readable_filter_and_order_are_allowed_and_preserved()
    {
        var contract = Contract();
        var options = new SemanticQueryOptions(
            Filter: new SemanticFieldFilter(Name, SemanticFilterOperator.Eq, "x"),
            Order: [new SemanticOrderTerm(Name, SemanticSortDirection.Asc)]);
        var graph = Resolve(contract, options);

        var authorized = new SemanticAuthorizer(new AllowAllSemanticAuthorizationPolicy()).Authorize(contract, graph);

        Assert.Equal(graph.Options, authorized.Options);
    }

    private sealed class DenyCustomerNamePolicy : AllowAllSemanticAuthorizationPolicy
    {
        public override bool CanAccessField(EntityId entityId, FieldId fieldId) =>
            entityId != Customer || fieldId != Name;
    }

    private sealed class DenyBalancePolicy : AllowAllSemanticAuthorizationPolicy
    {
        public override bool CanAccessField(EntityId entityId, FieldId fieldId) =>
            entityId != Account || fieldId != Balance;
    }
}
