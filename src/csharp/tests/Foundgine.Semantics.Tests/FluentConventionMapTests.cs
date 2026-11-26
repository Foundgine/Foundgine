using Foundgine.Core.Semantic.Configuration;
using Xunit;

namespace Foundgine.Semantics.Tests;

public sealed class FluentConventionMapTests
{
    private enum ItemKind { Standard, Premium }

    private readonly record struct ItemId(int Value);

    // Model and storage are the same type; the id is a strongly typed struct and two members are navigations.
    private sealed class Item
    {
        public ItemId Id { get; set; }
        public string Name { get; set; } = "";
        public decimal? Price { get; set; }
        public ItemKind Kind { get; set; }
        public Item? Parent { get; set; }
        public List<Item> Children { get; set; } = [];
    }

    private sealed class ItemModel : FoundgineModelConfiguration<Item>
    {
        public override void Configure(FoundgineModelBuilder<Item> model) => model.ToEntity("Item");
    }

    private sealed class ItemTable : FoundginePersistenceConfiguration<Item>
    {
        public override void Configure(FoundginePersistenceBuilder<Item> model) =>
            model.ToTable("items").HasKey(x => x.Id);
    }

    // Model and storage are different types: Name is stored as Title, Note has no storage member.
    private sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public string Note { get; set; } = "";
    }

    private sealed class ProductRow
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public decimal Price { get; set; }
    }

    private sealed class ProductModel : FoundgineModelConfiguration<Product>
    {
        public override void Configure(FoundgineModelBuilder<Product> model) => model.ToEntity("Product");
    }

    private sealed class ProductTable : FoundginePersistenceConfiguration<ProductRow>
    {
        public override void Configure(FoundginePersistenceBuilder<ProductRow> model) =>
            model.ToTable("products").HasKey(x => x.Id);
    }

    private sealed class ProductMap : FoundgineEntityMap<Product, ProductRow>
    {
        public override void Configure(FoundgineEntityMapBuilder<Product, ProductRow> map)
        {
            map.Property(x => x.Name).To(e => e.Title);
            map.Convention(x => x.Note);
        }
    }

    private sealed class StrictProductMap : FoundgineEntityMap<Product, ProductRow>
    {
        public override void Configure(FoundgineEntityMapBuilder<Product, ProductRow> map)
        {
            map.Property(x => x.Name).To(e => e.Title);
            map.Convention();
        }
    }

    private sealed class Gadget
    {
        public int Id { get; set; }
        public decimal Price { get; set; }
    }

    private sealed class GadgetRow
    {
        public int Id { get; set; }
        public double Price { get; set; }
    }

    private sealed class GadgetMap : FoundgineEntityMap<Gadget, GadgetRow>
    {
        public override void Configure(FoundgineEntityMapBuilder<Gadget, GadgetRow> map) => map.Convention();
    }

    private sealed class Model(params IFoundgineConfiguration[] configurations) : FoundgineModel
    {
        public override void Configure(FoundgineModelBuilder model)
        {
            foreach (var configuration in configurations)
                model.Configure(configuration);
        }
    }

    [Fact]
    public void Identity_map_maps_value_typed_members_in_declaration_order_and_skips_navigations()
    {
        var registry = new Model(new ItemModel(), new ItemTable(), new FoundgineIdentityEntityMap<Item>()).Build();

        var entity = registry.Entities.Single(e => e.Name == "Item");
        Assert.Equal(
            new[] { "Id", "Name", "Price", "Kind" },
            entity.EffectiveFields.Select(f => f.Name).ToArray());
        Assert.Equal(typeof(ItemId), entity.EffectiveFields.Single(f => f.Name == "Id").ClrType);
        Assert.Equal(typeof(decimal?), entity.EffectiveFields.Single(f => f.Name == "Price").ClrType);
    }

    [Fact]
    public void Explicit_mapping_is_kept_and_convention_fills_the_rest_except_ignored_members()
    {
        var registry = new Model(new ProductModel(), new ProductTable(), new ProductMap()).Build();

        var entity = registry.Entities.Single(e => e.Name == "Product");
        Assert.Equal(
            new[] { "Name", "Id", "Price" },
            entity.EffectiveFields.Select(f => f.Name).ToArray());

        var columns = entity.Columns.Select(c => c.Name).ToArray();
        Assert.Contains("Title", columns);
        Assert.DoesNotContain("Name", columns);
        Assert.DoesNotContain("Note", columns);
    }

    [Fact]
    public void Convention_rejects_a_model_member_without_a_storage_member()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new Model(new ProductModel(), new ProductTable(), new StrictProductMap()).Build());

        Assert.Contains("Product.Note", exception.Message);
    }

    [Fact]
    public void Convention_rejects_members_that_differ_in_type()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new Model(new GadgetMap()).Build());

        Assert.Contains("Gadget.Price", exception.Message);
        Assert.Contains("differs in type", exception.Message);
    }
}
