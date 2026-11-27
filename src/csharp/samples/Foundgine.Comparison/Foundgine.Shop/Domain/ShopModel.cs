using Foundgine.Core.Semantic.Configuration;
using Foundgine.Providers.EntityFrameworkCore;

namespace Foundgine.Shop.Domain;

/// <summary>
/// The whole Foundgine declaration. Only three things are stated; the rest is convention:
/// <list type="number">
///   <item>which models relate to which (<c>Semantic</c>),</item>
///   <item>the legacy table and column names (<c>Persistence</c>),</item>
///   <item>that each class is also its own storage type (<c>IdentityEntityMap</c>).</item>
/// </list>
/// Keys are the mapped <c>Id</c> members and foreign keys are found by name (<c>Order.CustomerId</c>,
/// <c>OrderLine.OrderId</c>, <c>OrderLine.ProductId</c>), so there is no key or connection configuration.
/// <c>Build()</c> validates the declaration at startup and reports every problem at once.
/// </summary>
public sealed class ShopModel : FoundgineModel
{
    public override void Configure(FoundgineModelBuilder model)
    {
        // Relationships. WithMany also declares the inverse, Customer.Orders.
        model.Semantic<Order>(s =>
        {
            s.HasRelationship(x => x.Customer).To<Customer>().WithMany(x => x.Orders);
            s.HasRelationship(x => x.Lines).To<OrderLine>();
        });
        model.Semantic<OrderLine>(s => s.HasRelationship(x => x.Product).To<Product>());

        // Legacy storage names.
        model.Persistence<Customer>(p =>
        {
            p.ToTable("tbl_customer");
            p.Property(x => x.Id).HasColumnName("cust_id");
            p.Property(x => x.FirstName).HasColumnName("first_nm");
            p.Property(x => x.LastName).HasColumnName("last_nm");
            p.Property(x => x.Email).HasColumnName("email_addr");
        });
        model.Persistence<Product>(p =>
        {
            p.ToTable("tbl_product");
            p.Property(x => x.Id).HasColumnName("prod_id");
            p.Property(x => x.Sku).HasColumnName("prod_sku");
            p.Property(x => x.Name).HasColumnName("prod_title");
            p.Property(x => x.Price).HasColumnName("list_price");
        });
        model.Persistence<Order>(p =>
        {
            p.ToTable("tbl_order");
            p.Property(x => x.Id).HasColumnName("ord_id");
            p.Property(x => x.CustomerId).HasColumnName("cust_id");
            p.Property(x => x.Status).HasColumnName("ord_status");
            p.Property(x => x.Total).HasColumnName("ord_total");
        });
        model.Persistence<OrderLine>(p =>
        {
            p.ToTable("tbl_order_line");
            p.Property(x => x.Id).HasColumnName("line_id");
            p.Property(x => x.OrderId).HasColumnName("ord_id");
            p.Property(x => x.ProductId).HasColumnName("prod_id");
            p.Property(x => x.Quantity).HasColumnName("qty");
            p.Property(x => x.UnitPrice).HasColumnName("unit_price");
        });

        // Model == storage type: every member maps to the member of the same name.
        model.Configure<IdentityEntityMap<Customer>>();
        model.Configure<IdentityEntityMap<Product>>();
        model.Configure<IdentityEntityMap<Order>>();
        model.Configure<IdentityEntityMap<OrderLine>>();
    }
}
