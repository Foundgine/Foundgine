package com.foundgine.samples.supplychain.model;

import com.foundgine.core.semantic.configuration.*;
import com.foundgine.samples.supplychain.domain.*;

/** Maps every semantic member onto the persistence member that holds it (each domain class is both, so the map is one-to-one). */
final class EntityMaps {
    private EntityMaps() {}

    static final class SupplierMap extends FoundgineEntityMap<Supplier, Supplier> {
        @Override
        public void configure(FoundgineEntityMapBuilder<Supplier, Supplier> map) {
            map.property(Supplier::getId).to(Supplier::getId);
            map.property(Supplier::getName).to(Supplier::getName);
            map.property(Supplier::getCountry).to(Supplier::getCountry);
            map.property(Supplier::getRiskScore).to(Supplier::getRiskScore);
            map.property(Supplier::getTenantId).to(Supplier::getTenantId);
        }
    }

    static final class WarehouseMap extends FoundgineEntityMap<Warehouse, Warehouse> {
        @Override
        public void configure(FoundgineEntityMapBuilder<Warehouse, Warehouse> map) {
            map.property(Warehouse::getId).to(Warehouse::getId);
            map.property(Warehouse::getBusinessUnitId).to(Warehouse::getBusinessUnitId);
            map.property(Warehouse::getName).to(Warehouse::getName);
            map.property(Warehouse::getTenantId).to(Warehouse::getTenantId);
        }
    }

    static final class ProductMap extends FoundgineEntityMap<Product, Product> {
        @Override
        public void configure(FoundgineEntityMapBuilder<Product, Product> map) {
            map.property(Product::getId).to(Product::getId);
            map.property(Product::getSku).to(Product::getSku);
            map.property(Product::getName).to(Product::getName);
            map.property(Product::getCategory).to(Product::getCategory);
            map.property(Product::getSafetyStock).to(Product::getSafetyStock);
        }
    }
}
