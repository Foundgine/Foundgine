package com.foundgine.samples.supplychain.advanced;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.semantic.resolution.RetrievalStrategy;
import com.foundgine.core.semantic.resolution.SemanticRetrievalRequest;
import com.foundgine.providers.storage.sql.retrieval.PostgresRetrievalCandidateSource;
import com.foundgine.providers.storage.sql.retrieval.PostgresRetrievalOptions;
import com.foundgine.samples.supplychain.advanced.semantics.SupplyChainSemanticModel;
import java.lang.reflect.Proxy;
import javax.sql.DataSource;
import org.junit.jupiter.api.Test;

/** Database-free counterpart of C# SupplyChainRetrievalCapabilityTests. */
final class SupplyChainPostgresRetrievalCapabilityParityTest {
    private static DataSource unreachableDataSource() {
        return (DataSource) Proxy.newProxyInstance(DataSource.class.getClassLoader(),
                new Class<?>[] {DataSource.class}, (proxy, method, args) -> {
                    throw new AssertionError("The capability gate should reject before opening PostgreSQL: " + method.getName());
                });
    }

    @Test
    void relational_is_a_documented_no_op() {
        var source = new PostgresRetrievalCandidateSource(unreachableDataSource(), SupplyChainSemanticModel.Metadata);
        var result = source.retrieve(new SemanticRetrievalRequest(SupplyChainSemanticModel.SUPPLIER,
                SupplyChainSemanticModel.field("Supplier", "Name"), "Acme", RetrievalStrategy.RELATIONAL));
        assertTrue(result.isEmpty());
    }

    @Test
    void vector_is_reserved_for_the_pgvector_lexical_provider() {
        var source = new PostgresRetrievalCandidateSource(unreachableDataSource(), SupplyChainSemanticModel.Metadata);
        var request = new SemanticRetrievalRequest(SupplyChainSemanticModel.PRODUCT,
                SupplyChainSemanticModel.field("Product", "Name"), "gasket", RetrievalStrategy.VECTOR);
        var error = assertThrows(UnsupportedOperationException.class, () -> source.retrieve(request));
        assertTrue(error.getMessage().toLowerCase().contains("pgvector"));
    }

    @Test
    void fuzzy_and_full_text_can_be_disabled_like_the_csharp_provider() {
        var source = new PostgresRetrievalCandidateSource(unreachableDataSource(), SupplyChainSemanticModel.Metadata,
                new PostgresRetrievalOptions(false, false, false, false, "english", "foundgine"));
        var fuzzy = new SemanticRetrievalRequest(SupplyChainSemanticModel.SUPPLIER,
                SupplyChainSemanticModel.field("Supplier", "Name"), "Acme Suplies", RetrievalStrategy.FUZZY);
        var fullText = new SemanticRetrievalRequest(SupplyChainSemanticModel.PRODUCT,
                SupplyChainSemanticModel.field("Product", "Name"), "hydraulic fitting", RetrievalStrategy.FULL_TEXT);
        assertThrows(UnsupportedOperationException.class, () -> source.retrieve(fuzzy));
        assertThrows(UnsupportedOperationException.class, () -> source.retrieve(fullText));
    }

    @Test
    void search_and_graph_similarity_are_opt_in_and_disabled_by_default() {
        var source = new PostgresRetrievalCandidateSource(unreachableDataSource(), SupplyChainSemanticModel.Metadata);
        var search = new SemanticRetrievalRequest(SupplyChainSemanticModel.SUPPLIER,
                SupplyChainSemanticModel.field("Supplier", "Name"), "acme", RetrievalStrategy.SEARCH);
        var graph = new SemanticRetrievalRequest(SupplyChainSemanticModel.SUPPLIER, null,
                "similar suppliers", RetrievalStrategy.GRAPH_SIMILARITY, 5, null,
                SupplyChainSemanticModel.relationship("Supplier", "purchaseOrders"), "1");
        assertThrows(UnsupportedOperationException.class, () -> source.retrieve(search));
        assertThrows(UnsupportedOperationException.class, () -> source.retrieve(graph));
    }

    @Test
    void graph_similarity_requires_relationship_and_reference_identity_when_enabled() {
        var options = new PostgresRetrievalOptions(true, true, false, true, "english", "foundgine");
        var source = new PostgresRetrievalCandidateSource(unreachableDataSource(), SupplyChainSemanticModel.Metadata, options);
        var missingRelationship = new SemanticRetrievalRequest(SupplyChainSemanticModel.SUPPLIER, null,
                "similar suppliers", RetrievalStrategy.GRAPH_SIMILARITY, 5, null, null, "1");
        var missingReference = new SemanticRetrievalRequest(SupplyChainSemanticModel.SUPPLIER, null,
                "similar suppliers", RetrievalStrategy.GRAPH_SIMILARITY, 5, null,
                SupplyChainSemanticModel.relationship("Supplier", "purchaseOrders"), null);
        assertTrue(assertThrows(IllegalArgumentException.class, () -> source.retrieve(missingRelationship))
                .getMessage().contains("Relationship"));
        assertTrue(assertThrows(IllegalArgumentException.class, () -> source.retrieve(missingReference))
                .getMessage().contains("ReferenceIdentity"));
    }
}
