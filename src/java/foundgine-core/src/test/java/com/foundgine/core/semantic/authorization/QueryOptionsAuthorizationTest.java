package com.foundgine.core.semantic.authorization;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.abstractions.*;
import com.foundgine.core.semantic.*;
import com.foundgine.core.semantic.ir.*;
import com.foundgine.core.semantic.query.*;

import org.junit.jupiter.api.Test;

import java.util.List;

/**
 * Filtering and ordering observe field values, so they need the same read access as selecting
 * them. Otherwise row count / row order leaks values the caller may not read (bisection).
 */
class QueryOptionsAuthorizationTest {
    private static final EntityId SUPPLIER = new EntityId(1);
    private static final EntityId CERT = new EntityId(2);
    private static final FieldId NAME = new FieldId(2);
    private static final FieldId RISK = new FieldId(3);
    private static final FieldId CERT_LEVEL = new FieldId(4);
    private static final RelationshipId CERTS = new RelationshipId(1);

    private static final SemanticContractSnapshot CONTRACT =
            new SemanticContractSnapshot(
                    new SemanticModelBuilder()
                            .entity(
                                    SUPPLIER,
                                    "Supplier",
                                    e ->
                                            e.identity(new FieldId(1), "Id")
                                                    .field(NAME, "Name", String.class)
                                                    .field(RISK, "RiskScore", Integer.class)
                                                    .relationship(
                                                            CERTS,
                                                            "Certs",
                                                            CERT,
                                                            RelationshipCardinality.MANY))
                            .entity(
                                    CERT,
                                    "Cert",
                                    e ->
                                            e.identity(new FieldId(1), "Id")
                                                    .field(CERT_LEVEL, "Level", Integer.class))
                            .build()
                            .freeze());

    private static SemanticOperation op(SemanticQueryOptions options) {
        return new SemanticOperation(
                new SemanticReadNode(
                        1, SUPPLIER, List.of(NAME), null, null, List.of(), options, null));
    }

    private static SemanticQueryOptions filter(SemanticFilterExpression f) {
        return new SemanticQueryOptions(f, List.of(), null, null, null);
    }

    private static void assertDenied(SemanticAuthorizer authorizer, SemanticQueryOptions o) {
        assertThrows(
                SemanticAuthorizationException.class, () -> authorizer.authorize(CONTRACT, op(o)));
        // The contract-less overload must not be a way around the check.
        assertThrows(SemanticAuthorizationException.class, () -> authorizer.authorize(op(o)));
    }

    private static class HideRisk extends AllowAllSemanticAuthorizationPolicy {
        @Override
        public boolean canAccessField(EntityId e, FieldId f) {
            return !(e.equals(SUPPLIER) && f.equals(RISK));
        }
    }

    private static class HideCertLevel extends AllowAllSemanticAuthorizationPolicy {
        @Override
        public boolean canAccessField(EntityId e, FieldId f) {
            return !(e.equals(CERT) && f.equals(CERT_LEVEL));
        }
    }

    private static class HideCerts extends AllowAllSemanticAuthorizationPolicy {
        @Override
        public boolean canAccessRelationship(EntityId e, RelationshipId r) {
            return !r.equals(CERTS);
        }
    }

    private static class HideCertEntity extends AllowAllSemanticAuthorizationPolicy {
        @Override
        public boolean canAccessEntity(EntityId id) {
            return !id.equals(CERT);
        }
    }

    private final SemanticAuthorizer hideRisk = new SemanticAuthorizer(new HideRisk());

    @Test
    void filterOnUnreadableFieldIsRefused() {
        assertDenied(
                hideRisk,
                filter(new SemanticFieldFilter(RISK, SemanticFilterOperator.GT, -1)));
    }

    @Test
    void filterOnUnreadableFieldNestedInBooleanCombinationsIsRefused() {
        var leaf = new SemanticFieldFilter(RISK, SemanticFilterOperator.GT, 5);
        var ok = new SemanticFieldFilter(NAME, SemanticFilterOperator.EQ, "x");
        assertDenied(hideRisk, filter(new SemanticAndFilter(List.of(ok, leaf))));
        assertDenied(hideRisk, filter(new SemanticOrFilter(List.of(ok, leaf))));
        assertDenied(
                hideRisk,
                filter(new SemanticAndFilter(List.of(ok, new SemanticOrFilter(List.of(leaf))))));
    }

    @Test
    void orderingByUnreadableFieldIsRefused() {
        assertDenied(
                hideRisk,
                new SemanticQueryOptions(
                        null,
                        List.of(new SemanticOrderTerm(RISK, SemanticSortDirection.ASC)),
                        null,
                        null,
                        null));
    }

    @Test
    void filterOrOrderOnReadableFieldStillWorks() {
        var options =
                new SemanticQueryOptions(
                        new SemanticFieldFilter(NAME, SemanticFilterOperator.EQ, "x"),
                        List.of(new SemanticOrderTerm(NAME, SemanticSortDirection.DESC)),
                        10,
                        null,
                        null);
        assertEquals(SUPPLIER, hideRisk.authorize(CONTRACT, op(options)).root().entityId());
    }

    @Test
    void relationshipFilterCannotReachUnreadableFieldOnTheTarget() {
        var policy = new SemanticAuthorizer(new HideCertLevel());
        var inner = new SemanticFieldFilter(CERT_LEVEL, SemanticFilterOperator.GT, 1);
        assertDenied(
                policy,
                filter(
                        new SemanticRelationshipFilter(
                                CERTS, SemanticRelationshipQuantifier.SOME, inner)));
    }

    @Test
    void aggregateFilterCannotReachUnreadableFieldOnTheTarget() {
        var policy = new SemanticAuthorizer(new HideCertLevel());
        assertThrows(
                SemanticAuthorizationException.class,
                () ->
                        policy.authorize(
                                CONTRACT,
                                op(
                                        filter(
                                                new SemanticAggregateFilter(
                                                        CERTS,
                                                        SemanticFilterAggregate.MAX,
                                                        CERT_LEVEL,
                                                        SemanticAggregateFilterOperator.GT,
                                                        1)))));
    }

    @Test
    void orderingThroughAPathCannotReachUnreadableFieldOnTheTarget() {
        var policy = new SemanticAuthorizer(new HideCertLevel());
        var term =
                new SemanticOrderTerm(
                        CERT_LEVEL,
                        SemanticSortDirection.ASC,
                        List.of(CERTS),
                        SemanticOrderAggregate.MAX);
        assertThrows(
                SemanticAuthorizationException.class,
                () ->
                        policy.authorize(
                                CONTRACT,
                                op(
                                        new SemanticQueryOptions(
                                                null, List.of(term), null, null, null))));
    }

    @Test
    void hiddenRelationshipAndHiddenTargetEntityCannotBeProbedThroughFilters() {
        var exists =
                filter(
                        new SemanticRelationshipFilter(
                                CERTS, SemanticRelationshipQuantifier.SOME, null));
        assertDenied(new SemanticAuthorizer(new HideCerts()), exists);
        assertThrows(
                SemanticAuthorizationException.class,
                () ->
                        new SemanticAuthorizer(new HideCertEntity())
                                .authorize(CONTRACT, op(exists)));
    }
}
