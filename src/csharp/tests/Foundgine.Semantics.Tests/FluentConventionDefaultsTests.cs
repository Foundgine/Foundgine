using Foundgine.Core.Semantic.Configuration;
using Foundgine.Core.Semantic.Metadata;
using Xunit;

namespace Foundgine.Semantics.Tests;

/// <summary>
/// The compiler's defaults: an entity map alone defines an entity, a mapped member named Id is the key, and a
/// relationship with no connection gets its foreign key from the member names.
/// </summary>
public sealed class FluentConventionDefaultsTests
{
    private readonly record struct AuthorId(int Value);

    private sealed class Thing
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class Keyless
    {
        public string Name { get; set; } = "";
    }

    private sealed class Author
    {
        public AuthorId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Book> Books { get; set; } = [];
    }

    private sealed class Book
    {
        public int Id { get; set; }
        public AuthorId AuthorId { get; set; }
        public Author Author { get; set; } = null!;
    }

    private sealed class Writer
    {
        public int Id { get; set; }
        public List<Essay> Essays { get; set; } = [];
    }

    private sealed class Essay
    {
        public int Id { get; set; }
        public int PenId { get; set; }
    }

    private sealed class AuthorModel : FoundgineModelConfiguration<Author>
    {
        public override void Configure(FoundgineModelBuilder<Author> model) =>
            model.HasRelationship(x => x.Books).To<Book>();
    }

    private sealed class BookModel : FoundgineModelConfiguration<Book>
    {
        public override void Configure(FoundgineModelBuilder<Book> model) =>
            model.HasRelationship(x => x.Author).To<Author>();
    }

    private sealed class WriterModel : FoundgineModelConfiguration<Writer>
    {
        public override void Configure(FoundgineModelBuilder<Writer> model) =>
            model.HasRelationship(x => x.Essays).To<Essay>();
    }

    private sealed class WriterConnections : FoundgineConnectionConfiguration
    {
        public override void Configure(FoundgineConnectionBuilder connections) =>
            connections.HasRelationship<Writer, Essay>()
                .Using<Writer, Essay>()
                .HasForeignKey(x => x.PenId)
                .References(x => x.Id);
    }

    private sealed class Model(params IFoundgineConfiguration[] configurations) : FoundgineModel
    {
        public override void Configure(FoundgineModelBuilder model)
        {
            foreach (var configuration in configurations)
                model.Configure(configuration);
        }
    }

    private static string ColumnName(EntityMetadata entity, ColumnReference reference) =>
        entity.Columns.Single(c => c.Id == reference.ColumnId).Name;

    [Fact]
    public void Entity_map_alone_defines_the_entity_and_Id_is_the_key()
    {
        var registry = new Model(new FoundgineIdentityEntityMap<Thing>()).Build();

        var entity = registry.Entities.Single();
        Assert.Equal("Thing", entity.Name);
        Assert.Equal("Thing", entity.EffectiveStorageName);
        Assert.Equal("Id", ColumnName(entity, entity.PrimaryKey!));
        Assert.Equal("Id", entity.Columns[0].Name);
    }

    [Fact]
    public void Inline_registration_configures_a_model_without_a_class()
    {
        var registry = new InlineModel().Build();

        var entity = registry.Entities.Single();
        Assert.Equal("Items", entity.Name);
        Assert.Equal("things", entity.EffectiveStorageName);
    }

    private sealed class InlineModel : FoundgineModel
    {
        public override void Configure(FoundgineModelBuilder model)
        {
            model.Semantic<Thing>(s => s.ToEntity("Items"));
            model.Persistence<Thing>(p => p.ToTable("things"));
            model.Configure(new FoundgineIdentityEntityMap<Thing>());
        }
    }

    [Fact]
    public void Missing_Id_and_missing_HasKey_is_reported()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new Model(new FoundgineIdentityEntityMap<Keyless>()).Build());

        Assert.Contains("Keyless: no key declared", error.Message);
    }

    [Fact]
    public void Foreign_keys_are_inferred_in_both_directions_from_member_names()
    {
        var registry = new Model(
            new AuthorModel(),
            new BookModel(),
            new FoundgineIdentityEntityMap<Author>(),
            new FoundgineIdentityEntityMap<Book>()).Build();

        var author = registry.Entities.Single(e => e.Name == "Author");
        var book = registry.Entities.Single(e => e.Name == "Book");

        var books = registry.Relationships.Single(r => r.Source == author.EntityId);
        Assert.True(books.IsCollection);
        Assert.Equal("Id", ColumnName(author, books.SourceKey));
        Assert.Equal("AuthorId", ColumnName(book, books.TargetKey));

        var owner = registry.Relationships.Single(r => r.Source == book.EntityId);
        Assert.False(owner.IsCollection);
        Assert.Equal("AuthorId", ColumnName(book, owner.SourceKey));
        Assert.Equal("Id", ColumnName(author, owner.TargetKey));
    }

    [Fact]
    public void Collection_side_alone_finds_the_key_named_after_the_source_type()
    {
        var registry = new Model(
            new AuthorModel(),
            new FoundgineIdentityEntityMap<Author>(),
            new FoundgineIdentityEntityMap<Book>()).Build();

        var author = registry.Entities.Single(e => e.Name == "Author");
        var book = registry.Entities.Single(e => e.Name == "Book");

        var relationship = Assert.Single(registry.Relationships);
        Assert.Equal("AuthorId", ColumnName(book, relationship.TargetKey));
        Assert.Equal("Id", ColumnName(author, relationship.SourceKey));
    }

    [Fact]
    public void Relationship_without_a_matching_key_name_still_needs_an_explicit_connection()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new Model(
            new WriterModel(),
            new FoundgineIdentityEntityMap<Writer>(),
            new FoundgineIdentityEntityMap<Essay>()).Build());

        Assert.Contains("no foreign key found by convention", error.Message);
    }

    [Fact]
    public void Explicit_connection_resolves_a_key_the_convention_cannot_find()
    {
        var registry = new Model(
            new WriterModel(),
            new WriterConnections(),
            new FoundgineIdentityEntityMap<Writer>(),
            new FoundgineIdentityEntityMap<Essay>()).Build();

        var essay = registry.Entities.Single(e => e.Name == "Essay");
        var relationship = Assert.Single(registry.Relationships);
        Assert.Equal("PenId", ColumnName(essay, relationship.TargetKey));
    }
}
