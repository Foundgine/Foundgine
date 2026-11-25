# Foundgine.Core.Semantic.Configuration

A fluent configuration API, similar in philosophy to EF Core's Fluent API but independent of it. It is a second way to produce the same `MetadataRegistry` that the `Foundgine*` attributes and the AOT generator produce; the two styles can be used side by side.

Each configuration class owns exactly one concern:

| Concern | Base class |
| --- | --- |
| Semantic entity, aliases, dimensions, relationships | `FoundgineModelConfiguration<TModel>` |
| Table, columns, key, indexes | `FoundginePersistenceConfiguration<TEntity>` |
| Model member to storage member | `FoundgineEntityMap<TModel, TEntity>` |
| Relationship to physical foreign key | `FoundgineConnectionConfiguration` |
| Model member to storage entity (connection), conversions, authorization | `FoundgineModelConnectionConfiguration<TModel>` |

`FoundgineModel` is the single registration point:

```csharp
public sealed class ErpModel : FoundgineModel
{
    public override void Configure(FoundgineModelBuilder model)
    {
        model.Configure<CustomerModelConfiguration>();
        model.Configure<OrderModelConfiguration>();
        model.Configure<ErpCustomerConfiguration>();
        model.Configure<ErpOrderConfiguration>();
        model.Configure<CustomerEntityMapping>();
        model.Configure<OrderEntityMapping>();      // every related model needs a map
        model.Configure<CustomerOrderConnection>();
        model.Configure<OrderModelConnections>();   // optional: model-to-entity connections
    }
}

MetadataRegistry registry = new ErpModel().Build();
```

`Build()` validates the whole set and throws one `InvalidOperationException` listing every problem (missing key, unmapped relationship, ambiguous connection, mismatched types, ...). Configuration order does not matter.

To combine with attribute-generated metadata, call `new ErpModel().BuildInto(GeneratedMetadata.Registry)`. It throws if an entity, model or relationship id already exists, so neither source can silently overwrite the other.

## Mapping by convention

Most entity maps are a list of `map.Property(x => x.A).To(x => x.A)` lines where both names and types match. Two shortcuts cover that:

```csharp
// Model and storage are the same type: nothing to write per entity.
model.Configure<FoundgineIdentityEntityMap<Customer>>();

// Different types: list the exceptions, let the convention map the rest.
public sealed class CustomerEntityMap : FoundgineEntityMap<Customer, CustomerRow>
{
    public override void Configure(FoundgineEntityMapBuilder<Customer, CustomerRow> map)
    {
        map.Property(x => x.Name).To(e => e.FullName);   // explicit: differing name
        map.Convention(x => x.DisplayNote);              // everything else by name and type; ignore this member
    }
}
```

`Convention(...)` maps each model member that is not mapped yet to the storage member with the same name and the same CLR type. Only value types (numbers, enums, dates, `Guid`, strongly typed id structs, nullable values) and `string` are considered, in declaration order; navigations and collections are never mapped. Call it after the explicit lines.

It is strict: a model member with no same-named storage member, or one of a different type, makes it throw and list every problem. Pass deliberate gaps to `Convention(...)` or map them explicitly. Reading the properties is done by reflection when the model is built (the explicit lambdas only read names), so keep the explicit form where the model must be built without reflection. `IdentityEntityMap<T>` in `Foundgine.Providers.EntityFrameworkCore` is the same map under its original name.

## Model connections, conversions and authorization

A model-to-entity connection (`ConnectionMetadata`) is the fluent counterpart of `[FoundgineConnection]`. Conversions and authorization predicates hang off it:

```csharp
public sealed class OrderModelConnections : FoundgineModelConnectionConfiguration<Order>
{
    public override void Configure(FoundgineModelConnectionBuilder<Order> connections) =>
        connections.HasConnection<CustomerERP>(x => x.Customer)      // name defaults to "Customer"
            .Maps(x => x.CustomerId, e => e.Id)                       // identical types
            .Maps(x => x.CustomerCode, e => e.CodeNumber, int.Parse)  // delegate conversion
            .Authorize<UserContext>("CanVisit",
                (user, customer) => user.TenantId == customer.TenantId);
}
```

- **Connection id** is `ConnectionId.Create(modelName, connectionName)`, the same as the generator. `HasName(...)` and `HasId(...)` override the name and the id.
- **Maps** `(source, target)` needs identical CLR types; `Maps(source, target, convert)` records a `ConversionMetadata` for the type pair with the delegate in `ConversionMetadata.Convert`, and the field's `Converter` is `fluent:<Source>-><Target>`. Foundgine never calls the delegate. One conversion exists per type pair; reuse the same delegate or the build reports a conflict.
- **Authorize** reads the expression tree (not the lambda body at run time) into `AuthorizationPredicate`. Supported: member access on the two parameters, constants, `==`, `!=`, `&&`, `||`, `!`. Anything else (method calls, captured variables) is reported by `Build()`. The first parameter is the caller context; the second is the connected entity. The id is derived from `modelName.connectionName` plus the authorization name unless one is passed.
- Target members used by `Maps` become columns on the target entity, and the target type must be the storage type of an entity map.

## Behaviour worth knowing

- **Identity** is derived exactly as the generator does: entity and field ids from semantic names, column ids from `schema.table` + column name. Fluent and attribute metadata for the same names therefore get the same ids.
- **Storage name** is `schema.table` when a schema is given; the SQL providers already split on `.`.
- **Defaults**: the semantic entity name defaults to the model type name, the table to the storage type name, the key to the mapped `Id`, a column to the member name, a dimension to the member name.
- **Fields and columns** exist only for members that are mapped (or used by a key, index or connection). There is no reflection over properties; the API only reads member names out of the lambdas.
- **Entity maps define entities**: a model with no `FoundgineModelConfiguration` gets its type name as the semantic entity name, and a storage type with no `FoundginePersistenceConfiguration` gets its type name as the table, so an entity map alone is enough. The same configuration can be written inline: `model.Semantic<Supplier>(s => s.HasAlias("Vendor", 95))` and `model.Persistence<Supplier>(p => p.ToTable("suppliers"))`.
- **Key by convention**: with no `HasKey`, a mapped storage member named `Id` is the primary key (its column comes first). `HasKey` is only needed for another member. A type with no `Id` and no `HasKey` is reported.
- **Foreign keys by convention**: a relationship with no connection gets its key from member names. The principal key is `Id`. A single-valued navigation `N` from S to T keeps the key on S, named `N + "Id"` or `T + "Id"`; a collection from S to T keeps it on T, named after T's single-valued navigation back to S (`N' + "Id"`) or `S + "Id"`. The member must exist on the storage type with the same type as the principal `Id`. Nothing is inferred for a self-relationship or when the two models are related more than once in either direction; those, and keys with other names, need an explicit connection. An explicit connection always wins.
- **Relationships**: `HasRelationship(x => x.Customer).To<Customer>().WithMany(x => x.Orders)` also defines the inverse `Customer.Orders`, unless `Customer` declares that navigation itself. One connection configures both directions of the pair. When two models are related more than once, pass the navigation: `connections.HasRelationship<Order, Customer>(x => x.BillingCustomer)`.
- **Foreign key side**: `HasForeignKey` selects on the target storage type (Customer 1 - * Order). Use `HasForeignKeyOnSource` when the source owns the key.
- **Index** is recorded as an access-path hint on the leading column (`FieldMetadata.IsIndexed`). Uniqueness is not modelled.
- `HasMaxLength` and `IsRequired` are stored on `ColumnMetadata.MaxLength` and `ColumnMetadata.IsRequired` (new init-only properties).

## Not covered yet

- Source-generator discovery of `FoundgineModel` subclasses (compile-time registry, no runtime lambda reading). Today `Build()` runs at startup.
- Composite keys and composite foreign keys.
- Type conversions on entity maps (`To(...)` and `Convention()` require identical CLR types); conversions are available on model connections only.
- Convention-based connection fields (matching members by name); list each `Maps(...)` explicitly.
