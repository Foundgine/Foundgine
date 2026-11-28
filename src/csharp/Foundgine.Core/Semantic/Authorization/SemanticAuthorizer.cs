using Foundgine.Core.Abstractions;
using Foundgine.Core.Semantic.Expressions;
using Foundgine.Core.Semantic.IR;
using Foundgine.Core.Semantic.IR.Graph;
using Foundgine.Core.Semantic.Query;

namespace Foundgine.Core.Semantic.Authorization;

/// <summary>
/// Applies authorization to an already-resolved semantic graph.
/// The result remains provider- and protocol-independent.
/// </summary>
public sealed class SemanticAuthorizer
{
    private readonly ISemanticAuthorizationPolicy _policy;

    public SemanticAuthorizer(ISemanticAuthorizationPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>
    /// Authorizes a resolved graph for entities, fields and relationships only.
    /// It does NOT check the filter or ordering carried in <c>graph.Options</c>; they are copied through unchanged.
    /// Use <see cref="Authorize(SemanticContractSnapshot, SemanticGraph)"/> for requests that can carry filters or ordering.
    /// </summary>
    public SemanticGraph Authorize(SemanticGraph graph) => AuthorizeGraph(null, graph);

    /// <summary>
    /// Authorizes a resolved graph, including the filter and ordering in <c>graph.Options</c>. Filtering and ordering
    /// observe field values, so they need the same read access as selecting them; a query that references a field or
    /// relationship the caller cannot read is refused, never silently ignored.
    /// </summary>
    public SemanticGraph Authorize(SemanticContractSnapshot contract, SemanticGraph graph)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Options is not null && graph.Nodes.Count > 0)
        {
            var root = graph.Nodes.FirstOrDefault(node => node.ParentId is null)
                       ?? throw new InvalidOperationException("Semantic graph has query options but no root node.");
            AuthorizeQueryOptions(contract, root.EntityId, graph.Options);
        }

        return AuthorizeGraph(contract, graph);
    }

    private SemanticGraph AuthorizeGraph(SemanticContractSnapshot? contract, SemanticGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (graph.Nodes.Count == 0)
            return graph;

        var authorized = new SemanticGraph();
        authorized.Options = graph.Options;
        var sourceToAuthorized = new Dictionary<int, SemanticGraphNode>();

        foreach (var sourceNode in graph.Nodes)
        {
            SemanticGraphNode? authorizedParent = null;
            if (sourceNode.ParentId is not null &&
                !sourceToAuthorized.TryGetValue(sourceNode.ParentId.Value, out authorizedParent))
            {
                // An ancestor was denied, so this node is unreachable.
                continue;
            }

            var fieldDecisions = sourceNode.Fields
                .Distinct()
                .Select(fieldId => (
                    FieldId: fieldId,
                    Decision: _policy.GetFieldAccess(
                        sourceNode.EntityId, fieldId, AuthorizationOperation.Read)))
                .ToArray();

            var fields = fieldDecisions
                .Where(x => x.Decision.IsAllowed)
                .Select(x => x.FieldId)
                .ToArray();

            var fieldAuthorization = SemanticAuthorizationCapabilityComposition.Compose(
                fieldDecisions.Where(x => x.Decision.IsAllowed).Select(x => x.Decision));

            var authorization = AuthorizationDecision.Combine(
                _policy.GetEntityAccess(sourceNode.EntityId, AuthorizationOperation.Read),
                AuthorizationDecisionFromPredicate(_policy.GetPredicate(
                    sourceNode.EntityId, AuthorizationOperation.Read)));
            authorization = AuthorizationDecision.Combine(authorization, fieldAuthorization);

            if (!authorization.IsAllowed)
            {
                if (sourceNode.ParentId is null)
                    throw new SemanticAuthorizationException(
                        $"Access denied for entity '{sourceNode.EntityId}'.");

                continue;
            }

            if (sourceNode.ViaRelationship is { } relationshipId)
            {
                if (sourceNode.ParentId is not { } parentId ||
                    !graph.Nodes.Any(node => node.Id == parentId))
                {
                    throw new InvalidOperationException(
                        $"Graph node {sourceNode.Id} has relationship '{relationshipId}' but no valid parent.");
                }

                var parentEntityId = graph.Nodes.Single(node => node.Id == parentId).EntityId;
                var relationshipDecision = _policy.GetRelationshipAccess(
                    parentEntityId, relationshipId, AuthorizationOperation.Read);
                if (!relationshipDecision.IsAllowed)
                {
                    // A denied relationship removes this node and its descendants.
                    continue;
                }

                authorization = AuthorizationDecision.Combine(authorization, relationshipDecision);
            }

            var sourcePredicate = sourceNode.Authorization;
            if (sourcePredicate is not null && authorization.Predicate != sourcePredicate)
            {
                authorization = AuthorizationDecision.Combine(
                    authorization,
                    AuthorizationDecisionFromPredicate(sourcePredicate));
            }

            var predicate = authorization.Predicate;
            SemanticGraphNode node;
            if (sourceNode.ParentId is null)
            {
                node = authorized.AddRoot(sourceNode.EntityId, fields, predicate);
            }
            else if (sourceNode.ViaRelationship is { } childRelationshipId)
            {
                node = authorized.Add(sourceNode.EntityId, childRelationshipId, authorizedParent, fields, predicate);
            }
            else if (sourceNode.ViaConnection is { } connectionId)
            {
                node = authorized.AddConnection(sourceNode.EntityId, connectionId, authorizedParent!, fields,
                    predicate);
            }
            else
            {
                throw new InvalidOperationException($"Graph node {sourceNode.Id} has a parent but no semantic edge.");
            }

            sourceToAuthorized[sourceNode.Id] = node;
        }

        return authorized;
    }


    /// <summary>
    /// Authorizes canonical Semantic IR directly. This is the authoritative
    /// semantic-to-planning authorization boundary: physical providers are
    /// deliberately not involved.
    /// </summary>
    /// <summary>
    /// Authorizes canonical Semantic IR against the trusted immutable contract.
    /// The snapshot is validated before policy evaluation so authorization can
    /// never silently reason about identities that are outside the contract
    /// used to resolve and plan the operation.
    /// </summary>
    /// <summary>
    /// Authorizes the complete canonical operation graph as one security object.
    /// Every reachable entity, field and relationship edge is evaluated before
    /// the graph is returned to planning. The provider is deliberately absent
    /// from this boundary.
    /// </summary>
    public SemanticOperationGraph Authorize(
        SemanticContractSnapshot contract,
        SemanticOperationGraph graph)
    {
        return AuthorizeGraphWithEvidence(contract, graph).Graph;
    }

    /// <summary>
    /// Authorizes the complete operation graph and returns evidence bound to the
    /// exact semantic contract and resulting authorized operation.
    /// </summary>
    public SemanticOperationGraphAuthorizationResult AuthorizeGraphWithEvidence(
        SemanticContractSnapshot contract,
        SemanticOperationGraph graph)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(graph);

        // Graph validation and contract validation happen before policy evaluation.
        // Converting back to canonical IR also prevents the graph authorizer from
        // becoming a second semantic representation with different security rules.
        var operation = graph.ToOperation();
        var authorized = AuthorizeWithEvidence(contract, operation);
        var authorizedGraph = SemanticOperationGraph.Create(authorized.Operation);

        return new SemanticOperationGraphAuthorizationResult(
            authorizedGraph,
            authorized.Evidence);
    }

    public SemanticOperation Authorize(
        SemanticContractSnapshot contract,
        SemanticOperation operation)
    {
        return AuthorizeWithEvidence(contract, operation).Operation;
    }

    /// <summary>
    /// Authorizes an operation and returns immutable evidence bound to the exact
    /// semantic contract used for the decision.
    /// </summary>
    public SemanticAuthorizationResult AuthorizeWithEvidence(
        SemanticContractSnapshot contract,
        SemanticOperation operation)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(operation);

        SemanticAuthorizationContractValidator.Validate(contract, operation);
        var authorized = AuthorizeOperation(contract, operation);
        var evidence = SemanticAuthorizationEvidence.Create(contract, authorized);
        return new SemanticAuthorizationResult(authorized, evidence);
    }

    /// <summary>
    /// Authorizes an operation without a contract. Filters and ordering that cross a relationship
    /// are refused here because their target entity cannot be established; use the contract overload.
    /// </summary>
    public SemanticOperation Authorize(SemanticOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return AuthorizeOperation(null, operation);
    }

    private SemanticOperation AuthorizeOperation(SemanticContractSnapshot? contract, SemanticOperation operation)
    {
        var root = AuthorizeNode(contract, operation.Root, isRoot: true);
        if (root is null)
            throw new SemanticAuthorizationException(
                $"Access denied for entity '{operation.Root.EntityId}'.");

        return new SemanticOperation(root);
    }

    private SemanticReadNode? AuthorizeNode(
        SemanticContractSnapshot? contract, SemanticReadNode node, bool isRoot)
    {
        var entity = _policy.GetEntityAccess(node.EntityId, AuthorizationOperation.Read);
        var predicate = _policy.GetPredicate(node.EntityId, AuthorizationOperation.Read);

        var effective = AuthorizationDecision.Combine(
            entity,
            predicate is null
                ? AuthorizationDecision.Allowed
                : AuthorizationDecision.Conditional(predicate));

        // A denied entity removes a child subtree; a denied root rejects the
        // operation. This mirrors graph authorization while keeping the
        // canonical Semantic IR as the authorization boundary.
        if (!effective.IsAllowed)
        {
            if (isRoot)
                throw new SemanticAuthorizationException(
                    $"Access denied for entity '{node.EntityId}'.");
            return null;
        }

        // Filtering and ordering observe field values, so they need the same read access as selecting
        // them. Otherwise the result shape (row count, order) reveals values the caller may not read.
        // Refuse rather than ignore: an ignored filter would return rows the caller meant to exclude.
        AuthorizeQueryOptions(contract, node);

        var fieldDecisions = node.Fields
            .Distinct()
            .Select(fieldId => (
                FieldId: fieldId,
                Decision: _policy.GetFieldAccess(
                    node.EntityId, fieldId, AuthorizationOperation.Read)))
            .ToArray();

        var fields = new List<FieldId>(fieldDecisions.Length);
        foreach (var field in fieldDecisions)
        {
            if (!field.Decision.IsAllowed)
                continue;

            fields.Add(field.FieldId);
            effective = AuthorizationDecision.Combine(effective, field.Decision);
        }

        if (node.Authorization is not null)
            effective = AuthorizationDecision.Combine(
                effective,
                AuthorizationDecision.Conditional(node.Authorization));

        if (!effective.IsAllowed)
        {
            if (isRoot)
                throw new SemanticAuthorizationException(
                    $"Authorization constraints denied semantic node '{node.Id}'.");
            return null;
        }

        var children = new List<SemanticReadNode>();
        foreach (var child in node.Children)
        {
            if (child.ViaRelationship is { } relationshipId)
            {
                var relationship = _policy.GetRelationshipAccess(
                    node.EntityId, relationshipId, AuthorizationOperation.Read);

                if (!relationship.IsAllowed)
                    continue;

                var childAuthorized = AuthorizeNode(contract, child, isRoot: false);
                if (childAuthorized is null)
                    continue;

                var combined = AuthorizationDecision.Combine(
                    relationship,
                    childAuthorized.Authorization is null
                        ? AuthorizationDecision.Allowed
                        : AuthorizationDecision.Conditional(childAuthorized.Authorization));

                children.Add(childAuthorized with
                {
                    Authorization = combined.Predicate
                });
                continue;
            }

            var authorizedChild = AuthorizeNode(contract, child, isRoot: false);
            if (authorizedChild is not null)
                children.Add(authorizedChild);
        }

        return node with
        {
            Fields = fields.ToArray(),
            Children = children,
            Authorization = effective.Predicate
        };
    }

    // ---- filter / order authorization -----------------------------------------------------------

    private void AuthorizeQueryOptions(SemanticContractSnapshot? contract, SemanticReadNode node)
    {
        if (node.QueryOptions is not { } options)
            return;

        AuthorizeQueryOptions(contract, node.EntityId, options);
    }

    private void AuthorizeQueryOptions(
        SemanticContractSnapshot? contract, EntityId rootEntity, SemanticQueryOptions options)
    {
        if (options.Filter is not null)
            CheckFilter(contract, rootEntity, options.Filter);

        foreach (var term in options.EffectiveOrder)
        {
            var entity = rootEntity;
            foreach (var relationship in term.EffectivePath)
                entity = Traverse(contract, entity, relationship);
            RequireReadableField(entity, term.Field);
        }
    }

    private void CheckFilter(
        SemanticContractSnapshot? contract, EntityId entity, SemanticFilterExpression filter)
    {
        switch (filter)
        {
            case SemanticFieldFilter field:
                RequireReadableField(entity, field.Field);
                break;
            case SemanticAndFilter and:
                foreach (var expression in and.Expressions) CheckFilter(contract, entity, expression);
                break;
            case SemanticOrFilter or:
                foreach (var expression in or.Expressions) CheckFilter(contract, entity, expression);
                break;
            case SemanticRelationshipFilter related:
                CheckFilter(contract, Traverse(contract, entity, related.Relationship), related.Predicate);
                break;
            case SemanticAggregateFilter aggregate:
                var target = Traverse(contract, entity, aggregate.Relationship);
                if (aggregate.Field is { } aggregateField) RequireReadableField(target, aggregateField);
                if (aggregate.Predicate is not null) CheckFilter(contract, target, aggregate.Predicate);
                break;
            default:
                // Anything not understood fails closed.
                throw new SemanticAuthorizationException(
                    $"Access denied: unsupported expression in query for entity '{entity}'.");
        }
    }

    private void RequireReadableField(EntityId entity, FieldId field)
    {
        if (!_policy.GetFieldAccess(entity, field, AuthorizationOperation.Read).IsAllowed)
            throw new SemanticAuthorizationException(
                $"Access denied: query references a field that is not readable on entity '{entity}'.");
    }

    /// <summary>Checks that the relationship and its target entity are readable; returns the target.</summary>
    private EntityId Traverse(SemanticContractSnapshot? contract, EntityId from, RelationshipId relationshipId)
    {
        if (contract is null)
            throw new SemanticAuthorizationException(
                "Access denied: relationship filters and ordering require a semantic contract.");
        if (!_policy.GetRelationshipAccess(from, relationshipId, AuthorizationOperation.Read).IsAllowed)
            throw new SemanticAuthorizationException(
                $"Access denied: query references a relationship that is not readable on entity '{from}'.");

        var target = contract.Get(from).Relationships.FirstOrDefault(r => r.Id == relationshipId)?.Target
                     ?? throw new ArgumentException(
                         $"Query references relationship '{relationshipId}' not declared on '{from}'.");
        if (!_policy.GetEntityAccess(target, AuthorizationOperation.Read).IsAllowed)
            throw new SemanticAuthorizationException($"Access denied for entity '{target}'.");
        return target;
    }

    private static AuthorizationDecision AuthorizationDecisionFromPredicate(AuthorizationPredicate? predicate) =>
        predicate is null
            ? AuthorizationDecision.Allowed
            : AuthorizationDecision.Conditional(predicate);
}
