package com.foundgine.samples.supplychain.model;

import com.foundgine.core.semantic.configuration.*;
import com.foundgine.samples.supplychain.domain.*;

/** Semantic side: entity names, aliases and dimensions. Says nothing about storage. */
final class SemanticConfigurations {
    private SemanticConfigurations() {}

    static final class SupplierSemantics extends FoundgineModelConfiguration<Supplier> {
        @Override
        public void configure(FoundgineModelBuilder<Supplier> model) {
            model.toEntity("Supplier");
            model.hasFieldNaming(FieldNaming.PASCAL_CASE);
            model.hasAlias("Vendor", 95);
            model.hasAlias("Seller", 90);
            model.hasDimension(Supplier::getCountry).as("country");
            model.hasDimension(Supplier::getTenantId).as("tenant");
        }
    }

    static final class WarehouseSemantics extends FoundgineModelConfiguration<Warehouse> {
        @Override
        public void configure(FoundgineModelBuilder<Warehouse> model) {
            model.toEntity("Warehouse");
            model.hasFieldNaming(FieldNaming.PASCAL_CASE);
            model.hasDimension(Warehouse::getBusinessUnitId).as("businessUnit");
            model.hasDimension(Warehouse::getTenantId).as("tenant");
        }
    }

    static final class ProductSemantics extends FoundgineModelConfiguration<Product> {
        @Override
        public void configure(FoundgineModelBuilder<Product> model) {
            model.toEntity("Product");
            model.hasFieldNaming(FieldNaming.PASCAL_CASE);
            model.hasDimension(Product::getCategory).as("category");
        }
    }
}
