package com.foundgine.core.semantic.authorization;

import com.foundgine.core.abstractions.*;
import com.foundgine.core.semantic.*;
import com.foundgine.core.semantic.ir.*;
import com.foundgine.core.semantic.ir.graph.*;
import com.foundgine.core.semantic.expressions.SemanticExpression;
import com.foundgine.core.semantic.query.*;

import java.util.*;

/** Applies authorization to resolved semantic graphs/operations before planning. */
public final class SemanticAuthorizer {
    private final ISemanticAuthorizationPolicy policy;

    public SemanticAuthorizer(ISemanticAuthorizationPolicy policy) {
        this.policy = Objects.requireNonNull(policy);
    }

    public SemanticGraph authorize(SemanticGraph graph) {
        Objects.requireNonNull(graph);
        if (graph.nodes().isEmpty()) return graph;
        var authorized = new SemanticGraph();
        authorized.setOptions(graph.options());
        var map = new HashMap<Integer, SemanticGraph.SemanticGraphNode>();
        for (var source : graph.nodes()) {
            SemanticGraph.SemanticGraphNode parent = null;
            if (source.parentId() != null) {
                parent = map.get(source.parentId());
                if (parent == null) continue;
            }
            var fieldDecisions =
                    source.fields().stream()
                            .distinct()
                            .map(
                                    f ->
                                            new AbstractMap.SimpleEntry<>(
                                                    f,
                                                    policy.getFieldAccess(
                                                            source.entityId(),
                                                            f,
                                                            AuthorizationOperation.READ)))
                            .toList();
            var fields =
                    fieldDecisions.stream()
                            .filter(x -> x.getValue().isAllowed())
                            .map(Map.Entry::getKey)
                            .toList();
            var fieldAuth =
                    SemanticAuthorizationCapabilityComposition.compose(
                            fieldDecisions.stream()
                                    .filter(x -> x.getValue().isAllowed())
                                    .map(Map.Entry::getValue)
                                    .toList());
            var authorization =
                    AuthorizationDecision.combine(
                            policy.getEntityAccess(source.entityId(), AuthorizationOperation.READ),
                            predicateDecision(
                                    policy.getPredicate(
                                            source.entityId(), AuthorizationOperation.READ)));
            authorization = AuthorizationDecision.combine(authorization, fieldAuth);
            if (!authorization.isAllowed()) {
                if (source.parentId() == null)
                    throw new SemanticAuthorizationException(
                            "Access denied for entity '" + source.entityId() + "'.");
                continue;
            }
            if (source.viaRelationship() != null) {
                if (source.parentId() == null
                        || graph.nodes().stream().noneMatch(n -> n.id() == source.parentId()))
                    throw new IllegalArgumentException(
                            "Graph node "
                                    + source.id()
                                    + " has relationship '"
                                    + source.viaRelationship()
                                    + "' but no valid parent.");
                var parentEntity =
                        graph.nodes().stream()
                                .filter(n -> n.id() == source.parentId())
                                .findFirst()
                                .orElseThrow()
                                .entityId();
                var rd =
                        policy.getRelationshipAccess(
                                parentEntity,
                                source.viaRelationship(),
                                AuthorizationOperation.READ);
                if (!rd.isAllowed()) continue;
                authorization = AuthorizationDecision.combine(authorization, rd);
            }
            if (source.authorization() != null
                    && !source.authorization().equals(authorization.predicate()))
                authorization =
                        AuthorizationDecision.combine(
                                authorization, predicateDecision(source.authorization()));
            SemanticGraph.SemanticGraphNode node;
            if (source.parentId() == null)
                node = authorized.addRoot(source.entityId(), fields, authorization.predicate());
            else if (source.viaRelationship() != null)
                node =
                        authorized.add(
                                source.entityId(),
                                source.viaRelationship(),
                                parent,
                                fields,
                                authorization.predicate());
            else if (source.viaConnection() != null)
                node =
                        authorized.addConnection(
                                source.entityId(),
                                source.viaConnection(),
                                parent,
                                fields,
                                authorization.predicate());
            else
                throw new IllegalArgumentException(
                        "Graph node " + source.id() + " has a parent but no semantic edge.");
            map.put(source.id(), node);
        }
        return authorized;
    }

    public SemanticOperationGraph authorize(SemanticContractSnapshot c, SemanticOperationGraph g) {
        return authorizeGraphWithEvidence(c, g).graph();
    }

    public SemanticOperationGraphAuthorizationResult authorizeGraphWithEvidence(
            SemanticContractSnapshot c, SemanticOperationGraph g) {
        Objects.requireNonNull(c);
        Objects.requireNonNull(g);
        var result = authorizeWithEvidence(c, g.toOperation());
        return new SemanticOperationGraphAuthorizationResult(
                SemanticOperationGraph.create(result.operation()), result.evidence());
    }

    public SemanticOperation authorize(SemanticContractSnapshot c, SemanticOperation op) {
        return authorizeWithEvidence(c, op).operation();
    }

    public SemanticAuthorizationResult authorizeWithEvidence(
            SemanticContractSnapshot c, SemanticOperation op) {
        Objects.requireNonNull(c);
        Objects.requireNonNull(op);
        SemanticAuthorizationContractValidator.validate(c, op);
        var authorized = authorizeOperation(c, op);
        return new SemanticAuthorizationResult(
                authorized, SemanticAuthorizationEvidence.create(c, authorized));
    }

    public SemanticOperation authorize(SemanticOperation op) {
        return authorizeOperation(null, op);
    }

    private SemanticOperation authorizeOperation(SemanticContractSnapshot c, SemanticOperation op) {
        Objects.requireNonNull(op);
        var root = authorizeNode(c, op.root(), true);
        if (root == null)
            throw new SemanticAuthorizationException(
                    "Access denied for entity '" + op.root().entityId() + "'.");
        return new SemanticOperation(root);
    }

    private SemanticReadNode authorizeNode(
            SemanticContractSnapshot contract, SemanticReadNode n, boolean root) {
        var effective =
                AuthorizationDecision.combine(
                        policy.getEntityAccess(n.entityId(), AuthorizationOperation.READ),
                        predicateDecision(
                                policy.getPredicate(n.entityId(), AuthorizationOperation.READ)));
        if (!effective.isAllowed()) {
            if (root)
                throw new SemanticAuthorizationException(
                        "Access denied for entity '" + n.entityId() + "'.");
            return null;
        }
        // Filtering and ordering observe field values, so they need the same read access as
        // selecting them. Otherwise result shape (row count, order) leaks values the caller may
        // not read, recoverable by bisection. Refuse rather than silently ignore: an ignored
        // filter would return rows the caller did not ask to be limited to.
        authorizeQueryOptions(contract, n);
        var fields = new ArrayList<FieldId>();
        for (var f : new LinkedHashSet<>(n.fields())) {
            var d = policy.getFieldAccess(n.entityId(), f, AuthorizationOperation.READ);
            if (d.isAllowed()) {
                fields.add(f);
                effective = AuthorizationDecision.combine(effective, d);
            }
        }
        if (n.authorization() != null && !n.authorization().equals(effective.predicate()))
            effective =
                    AuthorizationDecision.combine(effective, predicateDecision(n.authorization()));
        if (!effective.isAllowed()) {
            if (root)
                throw new SemanticAuthorizationException(
                        "Authorization constraints denied semantic node '" + n.id() + "'.");
            return null;
        }
        var children = new ArrayList<SemanticReadNode>();
        for (var child : n.children()) {
            if (child.viaRelationship() != null) {
                var rd =
                        policy.getRelationshipAccess(
                                n.entityId(), child.viaRelationship(), AuthorizationOperation.READ);
                if (!rd.isAllowed()) continue;
                var ca = authorizeNode(contract, child, false);
                if (ca == null) continue;
                var combined =
                        AuthorizationDecision.combine(
                                rd,
                                ca.authorization() == null
                                        ? AuthorizationDecision.ALLOWED
                                        : predicateDecision(ca.authorization()));
                children.add(ca.withAuthorization(combined.predicate()));
            } else {
                var ca = authorizeNode(contract, child, false);
                if (ca != null) children.add(ca);
            }
        }
        return new SemanticReadNode(
                n.id(),
                n.entityId(),
                fields,
                n.viaRelationship(),
                n.viaConnection(),
                children,
                n.queryOptions(),
                effective.predicate(),
                n.requiredFields());
    }

    // ---- filter / order authorization -------------------------------------------------------

    private void authorizeQueryOptions(SemanticContractSnapshot contract, SemanticReadNode n) {
        var options = n.queryOptions();
        if (options == null) return;
        if (options.filter() != null) checkFilter(contract, n.entityId(), options.filter());
        for (var term : options.effectiveOrder()) {
            var entity = n.entityId();
            for (var relationship : term.effectivePath())
                entity = traverse(contract, entity, relationship);
            if (term.field() != null) requireField(entity, term.field());
        }
    }

    private void checkFilter(
            SemanticContractSnapshot contract, EntityId entity, SemanticFilterExpression filter) {
        switch (filter) {
            case SemanticFieldFilter f -> requireField(entity, f.field());
            case SemanticAndFilter f ->
                    f.expressions().forEach(e -> checkFilter(contract, entity, e));
            case SemanticOrFilter f ->
                    f.expressions().forEach(e -> checkFilter(contract, entity, e));
            case SemanticRelationshipFilter f -> {
                var target = traverse(contract, entity, f.relationship());
                if (f.predicate() != null) checkFilter(contract, target, f.predicate());
            }
            case SemanticAggregateFilter f -> {
                var target = traverse(contract, entity, f.relationship());
                if (f.field() != null) requireField(target, f.field());
                if (f.predicate() != null) checkFilter(contract, target, f.predicate());
            }
            default -> checkExpression(contract, entity, filter);
        }
    }

    /** Generic expression walk; anything not understood fails closed. */
    private void checkExpression(
            SemanticContractSnapshot contract, EntityId entity, SemanticExpression e) {
        if (e == null) return;
        switch (e) {
            case SemanticFilterExpression f
                    when f instanceof SemanticFieldFilter
                            || f instanceof SemanticAndFilter
                            || f instanceof SemanticOrFilter
                            || f instanceof SemanticRelationshipFilter
                            || f instanceof SemanticAggregateFilter -> checkFilter(contract, entity, f);
            case SemanticExpression.Literal l -> {}
            case SemanticExpression.FieldReference r -> requireField(entity, r.field());
            case SemanticExpression.RelationshipReference r ->
                    traverse(contract, entity, r.relationship());
            case SemanticExpression.Path p -> {
                var current = entity;
                // The path's source is evaluated against the current entity.
                checkExpression(contract, entity, p.source());
                for (var r : p.relationships()) current = traverse(contract, current, r);
            }
            case SemanticExpression.Unary u -> checkExpression(contract, entity, u.operand());
            case SemanticExpression.Binary b -> {
                checkExpression(contract, entity, b.left());
                checkExpression(contract, entity, b.right());
            }
            case SemanticExpression.Logical l ->
                    l.operands().forEach(o -> checkExpression(contract, entity, o));
            case SemanticExpression.Aggregate a -> {
                checkExpression(contract, entity, a.source());
                checkExpression(contract, entity, a.argument());
            }
            case SemanticExpression.Function f ->
                    f.arguments().forEach(o -> checkExpression(contract, entity, o));
            default ->
                    throw new SemanticAuthorizationException(
                            "Access denied: unsupported expression in query for entity '"
                                    + entity
                                    + "'.");
        }
    }

    private void requireField(EntityId entity, FieldId field) {
        if (!policy.getFieldAccess(entity, field, AuthorizationOperation.READ).isAllowed())
            throw new SemanticAuthorizationException(
                    "Access denied: query references a field that is not readable on entity '"
                            + entity
                            + "'.");
    }

    /** Checks the relationship and its target entity are readable; returns the target. */
    private EntityId traverse(
            SemanticContractSnapshot contract, EntityId from, RelationshipId relationship) {
        if (contract == null)
            throw new SemanticAuthorizationException(
                    "Access denied: relationship filters and ordering require a semantic contract.");
        if (!policy.getRelationshipAccess(from, relationship, AuthorizationOperation.READ)
                .isAllowed())
            throw new SemanticAuthorizationException(
                    "Access denied: query references a relationship that is not readable on entity '"
                            + from
                            + "'.");
        var target =
                contract.get(from).relationships().stream()
                        .filter(r -> r.id().equals(relationship))
                        .map(SemanticRelationship::target)
                        .findFirst()
                        .orElseThrow(
                                () ->
                                        new IllegalArgumentException(
                                                "Query references relationship '"
                                                        + relationship
                                                        + "' not declared on '"
                                                        + from
                                                        + "'."));
        if (!policy.getEntityAccess(target, AuthorizationOperation.READ).isAllowed())
            throw new SemanticAuthorizationException(
                    "Access denied for entity '" + target + "'.");
        return target;
    }

    private static AuthorizationDecision predicateDecision(AuthorizationPredicate p) {
        return p == null ? AuthorizationDecision.ALLOWED : AuthorizationDecision.conditional(p);
    }
}
