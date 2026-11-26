package com.foundgine.samples.supplychain.advanced;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.foundgine.core.semantic.metadata.ModelMetadata;
import com.foundgine.samples.supplychain.advanced.model.SupplyChainFoundgineModel;
import java.util.stream.StreamSupport;
import org.junit.jupiter.api.Test;

/** Guards the fluent semantic configuration that the Advanced sample relies on. */
class FluentConfigurationCoverageTest {
    @Test
    void supplierModelCarriesItsConfiguredMinimumAliasEvidenceWeight() {
        var registry = new SupplyChainFoundgineModel().build();
        ModelMetadata supplier = StreamSupport.stream(registry.models().spliterator(), false)
                .filter(model -> model.name().equals("Supplier"))
                .findFirst()
                .orElseThrow();

        assertEquals(80, supplier.minimumWeight());
        assertTrue(registry.getEntity(supplier.entity()).effectiveAliases().stream()
                .anyMatch(alias -> alias.name().equals("Vendor") && alias.weight() == 95));
    }

    @Test
    void supplierFieldAliasIsConfiguredIndependentlyFromEntityAliasEvidence() {
        var model = com.foundgine.samples.supplychain.advanced.semantics.SupplyChainSemanticModel.MODEL;
        var supplier = model.get(com.foundgine.samples.supplychain.advanced.semantics.SupplyChainSemanticModel.SUPPLIER);
        var country = supplier.fields().stream()
                .filter(field -> field.name().equals("Country"))
                .findFirst()
                .orElseThrow();

        assertEquals(85, country.effectiveAliases().stream()
                .filter(alias -> alias.name().equals("State"))
                .findFirst()
                .orElseThrow()
                .weight());
        assertTrue(supplier.effectiveAliases().stream()
                .anyMatch(alias -> alias.name().equals("Vendor") && alias.weight() == 95));
    }

    @Test
    void purchaseOrderRelationshipAliasIsConfiguredOnTheRelationshipNotTheEntity() {
        var model = com.foundgine.samples.supplychain.advanced.semantics.SupplyChainSemanticModel.MODEL;
        var purchaseOrder = model.get(com.foundgine.samples.supplychain.advanced.semantics.SupplyChainSemanticModel.PURCHASE_ORDER);
        var supplierRelationship = purchaseOrder.relationships().stream()
                .filter(relationship -> relationship.name().equals("supplier"))
                .findFirst()
                .orElseThrow();

        assertEquals(85, supplierRelationship.effectiveAliases().stream()
                .filter(alias -> alias.name().equals("vendor"))
                .findFirst()
                .orElseThrow()
                .weight());
        assertTrue(purchaseOrder.effectiveAliases().stream()
                .anyMatch(alias -> alias.name().equals("PO") && alias.weight() == 100));
    }
}
