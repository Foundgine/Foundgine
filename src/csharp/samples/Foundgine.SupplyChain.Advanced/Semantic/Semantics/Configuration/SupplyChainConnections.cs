using Foundgine.Core.Semantic.Configuration;
using Foundgine.SupplyChain.Advanced.Domain;

namespace Foundgine.SupplyChain.Advanced.Semantics.Configuration;

// Relationships are realised as physical foreign keys by convention: a navigation N to T keeps its key on the
// side that owns it, named N + "Id" or T + "Id" (Warehouse.BusinessUnit -> Warehouse.BusinessUnitId; the collection
// Supplier.PurchaseOrders -> PurchaseOrder.SupplierId), and points at the target's Id. Only the relationships the
// convention cannot decide are written out: Product and ProductComponent are related three ways, so those
// connections name the navigation they configure.

public sealed class SupplyChainConnections : FoundgineConnectionConfiguration
{
    public override void Configure(FoundgineConnectionBuilder connections)
    {
        connections.HasRelationship<Product, ProductComponent>(x => x.Components)
            .Using<Product, ProductComponent>()
            .HasForeignKey(x => x.ParentProductId)
            .References(x => x.Id);

        connections.HasRelationship<Product, ProductComponent>(x => x.UsedInComponents)
            .Using<Product, ProductComponent>()
            .HasForeignKey(x => x.ComponentProductId)
            .References(x => x.Id);
    }
}
