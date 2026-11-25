using Foundgine.Core.Semantic.Configuration;
using Foundgine.SupplyChain.Domain.Models;

namespace Foundgine.SupplyChain.Domain;

// Relationship -> physical foreign key, inferred when the key is named after the navigation or the type
// (Shipment.Order -> Shipment.OrderId, SalesOrderLine.Product -> SalesOrderLine.ProductId, Customer.Orders ->
// SalesOrder.CustomerId). Only the two relationships whose keys use other names are written out. One connection
// configures both directions of a pair. HasForeignKey selects the key on the target type (one-to-many);
// HasForeignKeyOnSource selects it on the source (many-to-one).

internal sealed class SupplyChainConnections : FoundgineConnectionConfiguration
{
    public override void Configure(FoundgineConnectionBuilder connections)
    {
        // SalesOrder 1 - * SalesOrderLine
        connections.HasRelationship<SalesOrder, SalesOrderLine>()
            .Using<SalesOrder, SalesOrderLine>()
            .HasForeignKey(l => l.OrderId)
            .References(o => o.Id);

        // CatalogProduct 1 - * InventoryPosition
        connections.HasRelationship<CatalogProduct, InventoryPosition>()
            .Using<CatalogProduct, InventoryPosition>()
            .HasForeignKey(i => i.ProductId)
            .References(p => p.Id);
    }
}
