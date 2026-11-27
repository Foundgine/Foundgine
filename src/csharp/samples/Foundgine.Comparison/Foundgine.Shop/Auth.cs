using System.Security.Claims;
using Foundgine.Core.Abstractions;
using Foundgine.Core.Semantic.Authorization;
using Foundgine.Core.Semantic.Security.Execution;
using Foundgine.Extensions.Keycloak.Warrants;

namespace Foundgine.Shop;

/// <summary>Plain JWT -> Foundgine: validated claims become a short-lived signed warrant. The token is the only input.</summary>
public sealed class JwtSecurityContextProvider(IHttpContextAccessor accessor, IFoundgineWarrantIssuer issuer)
    : ISecurityExecutionContextProvider
{
    public SecurityExecutionContext? GetSecurityExecutionContext()
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        var sub = user.FindFirstValue("sub");
        var tenant = user.FindFirstValue("tenant_id");
        if (string.IsNullOrWhiteSpace(sub) || string.IsNullOrWhiteSpace(tenant)) return null;
        // A fresh warrant per call: the runtime consumes each warrant's nonce once.
        return issuer.Issue(sub, tenant, user.FindAll("role").Select(c => c.Value).ToArray());
    }
}

/// <summary>
/// Row-level rule, stated once and enforced at every entity boundary of the query plan, nested traversal included:
/// a customer only sees their own Customer row and their own Orders. Every other read is allowed here; writes are
/// denied by the interface defaults because this API is read-only.
/// </summary>
public sealed class ShopAuthorizationPolicy(IHttpContextAccessor accessor) : ISemanticAuthorizationPolicy
{
    private static readonly EntityId Customer = EntityId.Create("Customer");
    private static readonly EntityId Order = EntityId.Create("Order");

    public bool CanAccessEntity(EntityId entityId) => true;
    public bool CanAccessField(EntityId entityId, FieldId fieldId) => true;
    public bool CanAccessRelationship(EntityId sourceEntityId, RelationshipId relationshipId) => true;

    public AuthorizationPredicate? GetPredicate(EntityId entityId, AuthorizationOperation operation)
    {
        var user = accessor.HttpContext?.User;
        if (operation != AuthorizationOperation.Read || user?.IsInRole("customer") != true) return null;
        var member = entityId == Customer ? "Id" : entityId == Order ? "CustomerId" : null;
        if (member is null) return null;
        var row = AuthorizationPredicate.Member(AuthorizationPredicate.ResourceParameter("resource"), member);
        // Fail closed when the claim is missing.
        if (!int.TryParse(user.FindFirstValue("customer_id"), out _))
            return AuthorizationPredicate.Equal(row, AuthorizationPredicate.Constant("-1"));
        return AuthorizationPredicate.Equal(row, AuthorizationPredicate.Member(AuthorizationPredicate.ContextParameter("user"), "CustomerId"));
    }
}
