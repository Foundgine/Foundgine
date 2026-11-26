using Foundgine.Core.Semantic.Configuration;
using Foundgine.SupplyChain.Advanced.Domain;
using Foundgine.SupplyChain.Advanced.Semantics.Configuration;

namespace Foundgine.SupplyChain.Advanced.Semantics;

/// <summary>
/// Single registration point for the Supply Chain model: semantic configuration,
/// persistence configuration, identity entity maps and connections. <c>Build()</c> validates the
/// whole set and produces the metadata registry the rest of the sample consumes.
/// </summary>
public sealed class SupplyChainModel : FoundgineModel
{
    public override void Configure(FoundgineModelBuilder model)
    {
        // Semantic model. SupplierCertification, Shipment and CustomerOrderLine need nothing beyond their type name,
        // so their entity map alone defines them.
        model.Configure<CompanyModelConfiguration>();
        model.Configure<BusinessUnitModelConfiguration>();
        model.Configure<WarehouseModelConfiguration>();
        model.Configure<SupplierModelConfiguration>();
        model.Configure<SupplierSiteModelConfiguration>();
        model.Configure<ProductModelConfiguration>();
        model.Configure<ProductComponentModelConfiguration>();
        model.Configure<PurchaseOrderModelConfiguration>();
        model.Configure<PurchaseOrderLineModelConfiguration>();
        model.Configure<InventoryLotModelConfiguration>();
        model.Configure<CustomerOrderModelConfiguration>();
        model.Configure<InventoryMovementModelConfiguration>();
        model.Configure<ProductionOutputModelConfiguration>();
        model.Configure<ComplianceIncidentModelConfiguration>();

        // Persistence model
        model.Configure<CompanyPersistenceConfiguration>();
        model.Configure<BusinessUnitPersistenceConfiguration>();
        model.Configure<WarehousePersistenceConfiguration>();
        model.Configure<SupplierPersistenceConfiguration>();
        model.Configure<SupplierSitePersistenceConfiguration>();
        model.Configure<SupplierCertificationPersistenceConfiguration>();
        model.Configure<ProductPersistenceConfiguration>();
        model.Configure<ProductComponentPersistenceConfiguration>();
        model.Configure<PurchaseOrderPersistenceConfiguration>();
        model.Configure<PurchaseOrderLinePersistenceConfiguration>();
        model.Configure<ShipmentPersistenceConfiguration>();
        model.Configure<InventoryLotPersistenceConfiguration>();
        model.Configure<CustomerOrderPersistenceConfiguration>();
        model.Configure<CustomerOrderLinePersistenceConfiguration>();
        model.Configure<InventoryMovementPersistenceConfiguration>();
        model.Configure<ProductionOutputPersistenceConfiguration>();
        model.Configure<ComplianceIncidentPersistenceConfiguration>();

        // Model-to-persistence mapping. The domain records are also the storage shape, so every member maps to the
        // member of the same name (FoundgineIdentityEntityMap, i.e. map.Convention()); nothing to write per entity.
        model.Configure<FoundgineIdentityEntityMap<Company>>();
        model.Configure<FoundgineIdentityEntityMap<BusinessUnit>>();
        model.Configure<FoundgineIdentityEntityMap<Warehouse>>();
        model.Configure<FoundgineIdentityEntityMap<Supplier>>();
        model.Configure<FoundgineIdentityEntityMap<SupplierSite>>();
        model.Configure<FoundgineIdentityEntityMap<SupplierCertification>>();
        model.Configure<FoundgineIdentityEntityMap<Product>>();
        model.Configure<FoundgineIdentityEntityMap<ProductComponent>>();
        model.Configure<FoundgineIdentityEntityMap<PurchaseOrder>>();
        model.Configure<FoundgineIdentityEntityMap<PurchaseOrderLine>>();
        model.Configure<FoundgineIdentityEntityMap<Shipment>>();
        model.Configure<FoundgineIdentityEntityMap<InventoryLot>>();
        model.Configure<FoundgineIdentityEntityMap<CustomerOrder>>();
        model.Configure<FoundgineIdentityEntityMap<CustomerOrderLine>>();
        model.Configure<FoundgineIdentityEntityMap<InventoryMovement>>();
        model.Configure<FoundgineIdentityEntityMap<ProductionOutput>>();
        model.Configure<FoundgineIdentityEntityMap<ComplianceIncident>>();

        // Relationships to physical keys
        model.Configure<SupplyChainConnections>();

        // Model-to-entity connections with field maps and authorization
        model.Configure<SupplyChainModelConnections>();
    }
}
