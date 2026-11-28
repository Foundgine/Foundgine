package com.foundgine.core.semantic.authorization;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.abstractions.*;
import com.foundgine.core.semantic.*;
import com.foundgine.core.semantic.query.*;

import org.junit.jupiter.api.Test;

import java.util.List;

/**
 * Filters and ordering observe field values, so the contract-aware graph overload must refuse them
 * when the caller cannot read the field. The contract-less {@code authorize(SemanticGraph)} overload
 * does not check query options; these tests pin the behaviour of the overload that does. Mirrors the
 * C# {@code SemanticGraphQueryOptionsAuthorizationTests}.
 */
class GraphQueryOptionsAuthorizationTest {
    private static final EntityId CUSTOMER = new EntityId(1);
    private static final EntityId ACCOUNT = new EntityId(2);
    private static final FieldId ID = new FieldId(1);
    private static final FieldId NAME = new FieldId(2);
    private static final FieldId BALANCE = new FieldId(3);
    private static final RelationshipId ACCOUNTS = new RelationshipId(1);

    private static final SemanticContractSnapshot CONTRACT =
            new SemanticContractSnapshot(
                    new SemanticModelBuilder()
                            .entity(
                                    CUSTOMER,
                                    "Customer",
                                    e ->
                                            e.identity(ID, "Id")
                                                    .field(NAME, "Name", String.class)
                                                    .relationship(
                                                            ACCOUNTS,
                                                            "Accounts",
                                                            ACCOUNT,
                                                            RelationshipCardinality.MANY))
                            .entity(
                                    ACCOUNT,
                                    "Account",
                                    e ->
                                            e.identity(ID, "Id")
                                                    .field(BALANCE, "Balance", Integer.class))
                            .build()
                            .freeze());

    private static SemanticGraph graph(SemanticQueryOptions options) {
        var graph = new SemanticGraph();
        graph.addRoot(CUSTOMER, List.of(ID));
        graph.setOptions(options);
        return graph;
    }

    private static SemanticQueryOptions filter(SemanticFilterExpression f) {
        return new SemanticQueryOptions(f, List.of(), null, null, null);
    }

    private static class DenyCustomerName extends AllowAllSemanticAuthorizationPolicy {
        @Override
        public boolean canAccessField(EntityId e, FieldId f) {
            return !(e.equals(CUSTOMER) && f.equals(NAME));
        }
    }

    private static class DenyBalance extends AllowAllSemanticAuthorizationPolicy {
        @Override
        public boolean canAccessField(EntityId e, FieldId f) {
            return !(e.equals(ACCOUNT) && f.equals(BALANCE));
        }
    }

    @Test
    void filterOnUnreadableRootFieldIsRefused() {
        var graph = graph(filter(new SemanticFieldFilter(NAME, SemanticFilterOperator.EQ, "x")));
        assertThrows(
                SemanticAuthorizationException.class,
                () -> new SemanticAuthorizer(new DenyCustomerName()).authorize(CONTRACT, graph));
    }

    @Test
    void filterThroughRelationshipOnUnreadableFieldIsRefused() {
        var graph =
                graph(
                        filter(
                                new SemanticRelationshipFilter(
                                        ACCOUNTS,
                                        SemanticRelationshipQuantifier.SOME,
                                        new SemanticFieldFilter(
                                                BALANCE, SemanticFilterOperator.EQ, 0))));
        assertThrows(
                SemanticAuthorizationException.class,
                () -> new SemanticAuthorizer(new DenyBalance()).authorize(CONTRACT, graph));
    }

    @Test
    void orderByUnreadableFieldIsRefused() {
        var graph =
                graph(
                        new SemanticQueryOptions(
                                null,
                                List.of(new SemanticOrderTerm(NAME, SemanticSortDirection.ASC)),
                                null,
                                null,
                                null));
        assertThrows(
                SemanticAuthorizationException.class,
                () -> new SemanticAuthorizer(new DenyCustomerName()).authorize(CONTRACT, graph));
    }

    @Test
    void readableFilterAndOrderAreAllowedAndPreserved() {
        var options =
                new SemanticQueryOptions(
                        new SemanticFieldFilter(NAME, SemanticFilterOperator.EQ, "x"),
                        List.of(new SemanticOrderTerm(NAME, SemanticSortDirection.ASC)),
                        null,
                        null,
                        null);
        var graph = graph(options);
        var authorized =
                new SemanticAuthorizer(new AllowAllSemanticAuthorizationPolicy())
                        .authorize(CONTRACT, graph);
        assertEquals(options, authorized.options());
    }
}
