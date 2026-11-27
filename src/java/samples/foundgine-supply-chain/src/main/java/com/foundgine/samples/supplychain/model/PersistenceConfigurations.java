package com.foundgine.samples.supplychain.model;

import com.foundgine.core.semantic.configuration.*;
import com.foundgine.samples.supplychain.domain.*;

/**
 * Storage side: tables, columns, keys and indexes. This is the source of the database schema: the migration
 * tooling (foundgine-migrations) diffs it against the last snapshot and writes the Flyway migration, the way
 * EF derives migrations from its model. After changing anything here run {@code migrations add <Name>}.
 */
final class PersistenceConfigurations {
    private PersistenceConfigurations() {}

    static final class SupplierStorage extends FoundginePersistenceConfiguration<Supplier> {
        @Override
        public void configure(FoundginePersistenceBuilder<Supplier> model) {
            model.toTable("suppliers");
            model.hasKey(Supplier::getId);
            model.property(Supplier::getId).hasColumnName("supplier_id");
            model.property(Supplier::getName).hasColumnName("supplier_name").hasMaxLength(100).isRequired();
            model.property(Supplier::getCountry).hasColumnName("country").hasMaxLength(2).isRequired();
            model.property(Supplier::getRiskScore).hasColumnName("risk_score").hasPrecision(5, 2).isRequired();
            model.property(Supplier::getTenantId).hasColumnName("tenant_id").hasMaxLength(50).isRequired();
            model.hasIndex(Supplier::getTenantId);
        }
    }

    static final class WarehouseStorage extends FoundginePersistenceConfiguration<Warehouse> {
        @Override
        public void configure(FoundginePersistenceBuilder<Warehouse> model) {
            model.toTable("warehouses");
            model.hasKey(Warehouse::getId);
            model.property(Warehouse::getId).hasColumnName("warehouse_id");
            model.property(Warehouse::getBusinessUnitId).hasColumnName("business_unit_id").isRequired();
            model.property(Warehouse::getName).hasColumnName("warehouse_name").hasMaxLength(100).isRequired();
            model.property(Warehouse::getTenantId).hasColumnName("tenant_id").hasMaxLength(50).isRequired();
            model.hasIndex(Warehouse::getTenantId);
        }
    }

    static final class ProductStorage extends FoundginePersistenceConfiguration<Product> {
        @Override
        public void configure(FoundginePersistenceBuilder<Product> model) {
            model.toTable("products");
            model.hasKey(Product::getId);
            model.property(Product::getId).hasColumnName("product_id");
            model.property(Product::getSku).hasColumnName("sku").hasMaxLength(50).isRequired();
            model.property(Product::getName).hasColumnName("product_name").hasMaxLength(100).isRequired();
            model.property(Product::getCategory).hasColumnName("category").hasMaxLength(50).isRequired();
            model.property(Product::getSafetyStock).hasColumnName("safety_stock").hasPrecision(10, 2).isRequired();
            model.hasUniqueIndex(Product::getSku);
        }
    }
}
