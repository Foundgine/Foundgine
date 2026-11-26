package com.foundgine.samples.supplychain.advanced;

import static org.junit.jupiter.api.Assertions.*;

import com.foundgine.core.semantic.resolution.RetrievalStrategy;
import com.foundgine.core.semantic.resolution.SemanticRetrievalPlanner;
import com.foundgine.providers.storage.sql.retrieval.PostgresRetrievalOptions;
import java.util.EnumSet;
import java.util.Set;
import org.junit.jupiter.api.Test;

/**
 * Pins Java Advanced retrieval capabilities to the C# Advanced contract:
 * Fuzzy, FullText, Search, GraphSimilarity and Vector are approximate/provider-backed;
 * Relational remains the deterministic non-approximate path.
 */
final class SupplyChainRetrievalCapabilityParityTest {
    private static final Set<RetrievalStrategy> PROVIDER_BACKED = Set.of(
            RetrievalStrategy.FUZZY,
            RetrievalStrategy.FULL_TEXT,
            RetrievalStrategy.SEARCH,
            RetrievalStrategy.GRAPH_SIMILARITY,
            RetrievalStrategy.VECTOR);

    @Test
    void every_provider_backed_strategy_matches_the_csharp_advanced_capability_set() {
        var expected = EnumSet.allOf(RetrievalStrategy.class);
        expected.remove(RetrievalStrategy.RELATIONAL);
        assertEquals(expected, PROVIDER_BACKED);
        assertEquals(5, PROVIDER_BACKED.size());
        for (var strategy : PROVIDER_BACKED)
            assertTrue(SemanticRetrievalPlanner.requiresApproximateRetrieval(strategy), strategy.name());
        assertFalse(SemanticRetrievalPlanner.requiresApproximateRetrieval(RetrievalStrategy.RELATIONAL));
    }

    @Test
    void postgres_default_capabilities_match_csharp_defaults() {
        var defaults = new PostgresRetrievalOptions();
        assertTrue(defaults.enablePgTrgm());
        assertTrue(defaults.enableFullText());
        assertFalse(defaults.enablePgSearch());
        assertFalse(defaults.enableApacheAge());
        assertEquals("english", defaults.fullTextConfiguration());
    }

    @Test
    void vector_strategy_is_explicitly_delegated_to_the_pgvector_lexical_provider() {
        assertTrue(SemanticRetrievalPlanner.requiresApproximateRetrieval(RetrievalStrategy.VECTOR));
        assertEquals(RetrievalStrategy.VECTOR,
                SemanticRetrievalPlanner.select(null, RetrievalStrategy.VECTOR));
    }
}
