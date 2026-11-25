# Advanced sample configuration cookbook

This sample is deliberately layered: start with conventions and add explicit configuration only where the model needs more information. The examples below describe the fluent API that ships in `Foundgine.Core`; they do not imply that Foundgine creates database tables or executes arbitrary configuration delegates.

## Configuration ladder

| Level | Use it when | API / sample |
| --- | --- | --- |
| 1. Convention-only entity | The CLR type is already the semantic and storage shape | `FoundgineIdentityEntityMap<T>`; see the identity registrations in `Semantics/SupplyChainModel.cs` |
| 2. Inline semantic and persistence | A small model needs a few aliases, dimensions, tables or indexes | `model.Semantic<T>(...)` and `model.Persistence<T>(...)` |
| 3. Reusable configuration classes | Configuration is substantial, shared or should be tested independently | `FoundgineModelConfiguration<T>`, `FoundginePersistenceConfiguration<T>`, `FoundgineEntityMap<TModel,TEntity>` |
| 4. Separate model and storage types | The API/domain vocabulary differs from physical persistence | Explicit `Property(...).To(...)`, followed by strict `Convention(ignore...)` |
| 5. Explicit relationship topology | Names cannot safely infer the foreign key, especially multiple relationships between the same types | `FoundgineConnectionConfiguration` |
| 6. Cross-model connections | A semantic model member reaches a storage entity, needs field conversion or an authorization predicate | `FoundgineModelConnectionConfiguration<TModel>` |

## Semantic configuration

```csharp
model.Semantic<Supplier>(s =>
{
    s.ToEntity("Supplier");                 // optional; defaults to the model type name
    s.HasAlias("Vendor", 95);              // entity alias with evidence weight
    s.HasMinimumWeight(80);                 // minimum accepted alias weight (1..100)
    s.HasDimension(x => x.Country).As("country");
    s.Property(x => x.Name).HasAlias("TradingName", 80);
    s.HasRelationship(x => x.PurchaseOrders)
        .To<PurchaseOrder>().Named("purchaseOrders");
});
```

Available semantic operations:

- `ToEntity(name)` sets the semantic entity name.
- `HasAlias(name, weight?)` adds an entity alias; weights are 1–100.
- `HasMinimumWeight(weight)` sets the entity's alias evidence threshold; weights are 1–100.
- `IsEvent()` marks an event without naming its time member; `IsEvent(x => x.OccurredAt)` also identifies the temporal member.
- `HasDimension(x => x.Member).As("dimension")` maps a model member to a semantic dimension.
- `Property(x => x.Member).HasAlias(name, weight?)` adds a field alias.
- `HasRelationship(x => x.Navigation).To<TTarget>()` declares a logical relationship; complete it with `Named`, `HasAlias`, `WithMany` or `WithOne` as appropriate.

## Persistence configuration

```csharp
model.Persistence<Supplier>(p =>
{
    p.ToTable("suppliers", "public");
    p.Property(x => x.Name).HasColumnName("supplier_name")
        .HasMaxLength(200).IsRequired();
    p.HasIndex(x => x.Country);
    p.HasIndex(x => new { x.TenantId, x.Country });
});
```

Available persistence operations in this C# API:

- `ToTable(name, schema?)` sets the table and optional schema.
- `HasKey(x => x.Member)` declares the key when it is not the default `Id` member.
- `Property(x => x.Member).HasColumnName(name)` records a physical column name.
- `HasMaxLength(length)` records a maximum length; `IsRequired(bool = true)` records requiredness.
- `HasIndex(...)` records one or more members as an access-path hint. It does not create an index itself and does not model uniqueness.

The persistence configuration is provider-independent metadata. A database migration or ORM provider remains responsible for applying physical schema changes.

## Model-to-storage mapping

When model and storage members have the same names and types, register `FoundgineIdentityEntityMap<T>`. When they differ, map the exceptions explicitly and then use the strict convention for the rest:

```csharp
public sealed class SupplierMap : FoundgineEntityMap<Supplier, SupplierRow>
{
    public override void Configure(FoundgineEntityMapBuilder<Supplier, SupplierRow> map)
    {
        map.Property(x => x.Name).To(row => row.DisplayName);
        map.Convention(x => x.ComputedLabel); // deliberate non-persisted member
    }
}
```

`Convention(ignore...)` maps remaining value-typed and string members with the same name and CLR type. It fails with a list of mismatches rather than silently dropping fields. Explicit mappings must come first. The convention path inspects public properties when the model is built; use explicit mappings when trimming/AOT constraints require avoiding that reflection path.

## Relationships and connections

A semantic relationship (`HasRelationship`) describes the logical traversal. A physical relationship (`HasRelationship<TSource,TTarget>`) describes how storage rows join. Foundgine may infer the common `MemberId`/`TargetId` patterns; write a `SupplyChainConnections` entry when multiple relationships between the same types or non-standard keys make inference ambiguous.

```csharp
connections.HasRelationship<Product, ProductComponent>(x => x.Components)
    .Using<Product, ProductComponent>()
    .HasForeignKey(x => x.ParentProductId)
    .References(x => x.Id);
```

A model-to-entity connection is different again: it describes how a semantic model member reaches an entity and who may follow it.

```csharp
connections.HasConnection<Supplier>(x => x.SupplierId)
    .HasName("supplier")
    .Maps(x => x.SupplierId, x => x.Id)
    .Authorize<SupplierAccessContext>(
        "SameTenantSupplier",
        (context, supplier) => context.TenantId == supplier.TenantId);
```

`Maps(source, target)` supports same-type correspondence. The overload with a converter records conversion metadata for the source/target type pair; Foundgine records the conversion and does not invoke it as an implicit side effect. `Authorize<TContext>` accepts the documented expression subset (member access, constants, equality/inequality, boolean AND/OR and NOT). It is a metadata policy declaration, not a replacement for the host's authentication or execution-time enforcement.

## Transport and protocol setup

The Advanced sample exposes the same application intent through three transports. Configure the model and intent handler once; attach protocol adapters at the host boundary:

| Protocol | Endpoint | Adapter |
| --- | --- | --- |
| REST / HTTP | `POST /intent`, `POST /intent/explain`, `GET /intent/capabilities` | `FoundgineIntentEndpoints` |
| GraphQL | `POST /graphql`, field `foundgineIntent(intent:, mode:)` | `FoundgineIntentGraphQLResolver` |
| MCP | `POST /mcp`, tool `foundgine_intent` | `FoundgineIntentMcpTools` |

The transport adapters share `IFoundgineIntentHandler`. They do not own semantic metadata, authorization policy or persistence. The host supplies the authenticated principal; caller identity must never be accepted from the intent body. The development-only demo identity middleware is not production authentication.

The separate MCP authorization lab tools (`read_entity`, `write_entity`, `policy_probe`) are sample-specific demonstrations, not additional generic intent protocols.

## Coverage boundary

This cookbook covers the public C# fluent configuration methods present in this repository snapshot. It intentionally does not claim C# parity with every Java persistence extension (for example named/unique indexes, PostgreSQL CHECK/foreign-key declarations, index access methods or extension requirements). Those Java capabilities are documented in the Java advanced sample and remain a cross-language parity backlog unless/until corresponding C# APIs are added.
