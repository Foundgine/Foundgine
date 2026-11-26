# Java Advanced sample — fluent configuration cookbook

The Java Advanced sample uses the fluent configuration API, not metadata annotations, as its application model source. Java method references (`Supplier::country`, `Warehouse::id`) replace C# expression trees. `SupplyChainFoundgineModel` is the single registration point; the semantic, persistence, entity-map, physical-relationship and model-connection configurations remain separate.

## From simple to complex

1. **Convention/defaults.** Omit `toEntity` when the Java type name is the semantic name. Omit `toTable` when the storage type name is the table name. Declare a key explicitly with `hasKey` when it is not the default key convention.
2. **Inline semantic setup.** Use `model.semantic(Type.class, s -> ...)` / per-type configuration classes for aliases, dimensions, event markers and relationships.
3. **Reusable configuration classes.** Extend `FoundgineModelConfiguration<T>`, `FoundginePersistenceConfiguration<T>`, `FoundgineEntityMap<TModel,TEntity>`, `FoundgineConnectionConfiguration` or `FoundgineModelConnectionConfiguration<TModel>`.
4. **Separate model and storage types.** Use `FoundgineEntityMapBuilder.property(Model::member).to(Storage::member)` for each mapped member. Unlike the C# API in this snapshot, Java's entity-map builder does not expose the reflection-based `convention(ignore...)` mapper.
5. **Non-inferable physical relationships.** Declare the navigation, source/target row types and foreign-key/reference members explicitly with `FoundgineConnectionBuilder`.
6. **Cross-model connections.** Add `hasConnection`, `maps` (same-type or converted), and `authorize` for field correspondence and caller/resource constraints.

## Semantic options

```java
model.toEntity("Supplier");
model.hasFieldNaming(FieldNaming.PASCAL_CASE);
model.hasAlias("Vendor", 95);
model.hasAlias("Seller", 90);
model.hasMinimumWeight(80);
model.hasDimension(Supplier::country).as("country");
model.property(Supplier::country).hasAlias("State", 85);
model.hasMany("purchaseOrders", PurchaseOrder.class);
```

Available options include `toEntity`, weighted `hasAlias`, `hasMinimumWeight`, `hasFieldNaming(AS_DECLARED|PASCAL_CASE)`, `isEvent()` / `isEvent(Type::occurredAt)`, `hasDimension(...).as(...)`, field `property(...).hasAlias(...)`, `hasMany` / `hasOne`, and navigation-based `hasRelationship(...).to(...)` with `named`, `hasAlias`, `withMany` and `withOne` (including named inverses).

Java method references are intentionally constrained: Foundgine resolves a direct getter/record accessor, not an arbitrary lambda expression. For computed or multi-member expressions, define explicit model/storage members or use the relevant explicit builder API.

## Persistence options

```java
model.toTable("products");
model.hasKey(Product::id);
model.property(Product::sku).hasColumnName("sku").hasMaxLength(64).isRequired();
model.property(Product::unitPrice).hasPrecision(18, 2).isRequired();
model.property(Product::reserved).hasDefault("0").isRequired();
model.hasUniqueIndex("products_sku_key", Product::sku);
model.hasCheck("ck_products_unit_price", "unit_price >= 0");
model.hasForeignKey("fk_lots_product", "products", "product_id", "NO_ACTION", "NO_ACTION", InventoryLot::productId);
```

The Java persistence builder in this repository also supports:

- `toTable(name, schema)` and `hasKey(member)`.
- Column name, maximum length, SQL type, decimal precision/scale, requiredness, trusted SQL defaults, and PostgreSQL `GENERATED ALWAYS` / `BY DEFAULT AS IDENTITY`.
- Plain and named indexes; unique indexes; composite indexes; PostgreSQL index access method/operator class through `hasIndexUsing`.
- Named CHECK constraints and named single/composite foreign keys with `NO_ACTION`, `RESTRICT`, `CASCADE`, `SET_NULL` or `SET_DEFAULT` actions.
- PostgreSQL extension requirements through `hasExtension`.

SQL defaults, CHECK expressions, index methods/operator classes and extension names are trusted model-authored schema inputs. Review them as migration code; do not pass caller-controlled values into these APIs. Metadata declarations alone do not apply migrations.

## Entity maps, physical relationships and model connections

Entity maps describe model-member to storage-member correspondence. Physical relationship configuration describes foreign-key topology. Model connections describe semantic model-member to entity correspondence and its authorization rule. These are separate layers and should not be collapsed into one mapping abstraction.

```java
connections.hasConnection("supplier", Supplier.class)
    .maps(PurchaseOrder::supplierId, Supplier::id)
    .authorize(Authorization.Context.class, "SameTenantSupplier",
        a -> a.context(Authorization.Context::tenantId)
              .eq(a.resource(Supplier::tenantId)));
```

`maps(source, target, converter)` records a typed conversion descriptor; it does not silently execute arbitrary conversion code during discovery. `authorize` uses the typed `AuthorizationOperands` / `AuthorizationExpression` DSL and lowers only the supported expression subset (member access, constants, equality/inequality, AND/OR/NOT). Authorization metadata is not a substitute for authenticating the caller or enforcing policy at execution.

## Protocol and application integration

Protocol coverage must be read at two levels: adapter availability in the Java libraries, and transports actually mounted by this sample.

| Protocol / integration | Java capability | Advanced sample status |
| --- | --- | --- |
| HTTP semantic intent | The sample's JDK `HttpServer` adapter | Runnable: `POST /intent`, `/intent/explain`, `/intent/capabilities`, `/intent/mutation` |
| MCP over HTTP | The sample's JDK `HttpServer` JSON-RPC endpoint | Runnable: `POST /mcp`, including `foundgine_intent` and `foundgine_mutation`; client demo included |
| GraphQL semantic API | Implemented in `foundgine-extensions` (`GraphQLSchemaAdapter`, query/mutation executors and `GraphQLHttpHandler`); the Java comparison `foundgine-shop` sample mounts the schema-driven adapter | Available as a separate, schema-driven GraphQL API in the comparison sample |
| GraphQL intent API | Advanced sample's `AdvancedGraphQLIntentEndpoint` | Runnable: `POST /graphql`, fields `foundgineIntent` and `foundgineMutation`; delegates to the same runtime handler as HTTP and MCP |
| PostgreSQL | JDBC-backed Advanced store and ambiguity-search integration | Runnable when the documented PostgreSQL environment is configured |
| JPA / Hibernate | `FoundgineOrm` provider integration | Demonstrated by the Advanced JPA path |

The Advanced sample mounts focused read and mutation intent operations over HTTP, GraphQL and MCP. This is not a replacement for the full schema-driven GraphQL adapter: use `foundgine-extensions` and the comparison shop sample for schema-derived entity queries and mutations. All three Advanced transports delegate to one `IFoundgineIntentHandler<IntentCaller>` implementation. Both Java and C# keep identity and authorization context host-owned rather than accepting it from the intent payload.

## Cross-language parity notes

The fluent configuration model is conceptually aligned, but the APIs are not yet feature-identical. Java currently has richer persistence schema declarations (SQL types, precision, defaults, identity, named/unique indexes, CHECKs, foreign keys, access methods and extensions). C# currently has the strict reflection-based entity-map `Convention(ignore...)` helper, which Java lacks. Java has explicit field naming modes; C# currently derives field names from its configuration path. Keep these differences visible in documentation and tests; do not imply one language supports an option merely because the other does.

## Runtime intent boundary: one handler for every protocol

The Java runtime exposes one application-owned `IFoundgineIntentHandler<TPrincipal>` for both
read and mutation intents. Read requests use `FoundgineIntentRequest`; writes use
`FoundgineMutationIntentRequest` and the existing `SemanticMutationRequest` / `IFoundgineMutations`
lifecycle. Both return the same transport-neutral `FoundgineIntentResult`.

- HTTP exposes read intent at `/intent` and mutation commands at `/intent/mutation`.
- GraphQL exposes `foundgineIntent` and `foundgineMutation` from the same `/graphql` endpoint.
- MCP exposes `foundgine_intent` and `foundgine_mutation`; both delegate through the same handler.
- Read operations include `EXECUTE`, `EXPLAIN` and `CAPABILITIES`. Mutation JSON supports `mode`
  values `execute` (default), `dry_run`, `approve` and `execute_approved`.
- The sample approval flow returns a random opaque `approvalId`; the server stores the approval for
  ten minutes, binds it to the approving tenant and role, consumes it once, and refuses replay or
  cross-caller use. This in-memory store is a demonstration; production deployments should use
  durable, distributed approval storage with explicit audit and revocation semantics.
- The mutation handler resolves the mutation engine from the trusted caller, so tenant and role
  policy are selected by the host rather than by fields in the mutation payload. Unknown command
  properties (including `tenantId`, `role` and `approvedBy`) are rejected; `actor` is accepted only
  for legacy compatibility and ignored in favor of the host-derived caller.
- `FoundgineMutationIntentRequest` accepts a `MutationPlanApproval` only as a trusted runtime object.
  The sample transports expose only opaque approval handles and keep approval objects server-side;
  never deserialize an approval object from caller-controlled JSON.
- The generic principal type keeps `foundgine-runtime` independent of Spring Security, Jakarta
  Security, servlet APIs, and identity-provider SDKs. A production host passes its verified principal.
- The shared handler does not replace execution-time authorization, provider conformance checks,
  warrant verification, replay protection, or domain-level transaction guarantees.

Example boundary implementation:

```java
final class ApplicationIntentHandler implements IFoundgineIntentHandler<MyPrincipal> {
    public CompletionStage<FoundgineIntentResult> handleAsync(
            FoundgineIntentRequest request, MyPrincipal principal, CancellationToken token) {
        // Resolve the read engine from the verified principal, then execute/explain/capabilities.
    }

    public CompletionStage<FoundgineIntentResult> handleMutationAsync(
            FoundgineMutationIntentRequest request, MyPrincipal principal, CancellationToken token) {
        // Resolve the mutation engine from the same verified principal and preserve the
        // dry-run -> approve -> execute-approved lifecycle.
    }
}
```

The sample's `IntentCaller` remains explicitly demo-only: its HTTP headers illustrate caller
partitioning, not production authentication. A production host should pass its authenticated
principal type and derive tenant and role claims from verified identity evidence.


## Warrant-backed mutation trust and revocation

For warrant-backed mutations, configure the key resolver, trusted issuer, replay store and revocation
store. The Java runtime now applies the same fail-closed revocation boundary as C#: it checks
revocation before planning, revalidates trust and revocation immediately before dispatch, consumes
the single-use replay key only after those checks, and verifies that the revocation sequence did not
change during execution. Missing revocation storage is a configuration failure for warrant-backed
mutation planning/execution.

Delegated warrants require the full root-to-leaf chain to be attached to the trusted
`SecurityExecutionContext` with `withDelegationChain(...)`, plus an explicit root issuer and
`ISecurityWarrantDelegationTrustResolver`. The runtime verifies every signature and delegation
edge and rejects authority expansion. `HighAssurance` provides an in-memory development revocation
store; replace it with durable shared storage for production deployments.


## Correlating HTTP, GraphQL and MCP execution

Send `X-Correlation-ID` on the transport request to associate protocol-level activity with the resulting execution receipt. The sample validates the value against `[A-Za-z0-9._:-]{1,128}`; when omitted it generates a UUID. The receipt exposes this as `correlationId`, while `requestId` remains a unique execution ID. Never use correlation IDs as authentication, tenant claims, authorization evidence or idempotency keys.

The Java runtime records read success, provider failure and pre-dispatch denial through the configured `IAuditLog`. `FoundgineOptions.auditLog(...)` allows the host to supply a durable sink; the default in-memory implementation is for development and tests only.


For externally anchored audit history, implement `IAuditCheckpointSigner` with a protected signing service, periodically call `HashChainedAuditLog.createCheckpoint(...)`, and store the returned `AuditCheckpoint` outside the audit database. Verify with `HashChainedAuditLog.verifyCheckpoint(...)` and compare the chain at the anchored sequence. Do not store signing key material in application configuration or in the audit database.


The sample's `AdvancedMcpFacade.auditLog()` exposes the shared hash-chained in-memory event stream for tests and diagnostics. It includes the protocol facade's approval-handle denials as well as runtime execution events. This is deliberately not a durable production audit store; replace the backing `IAuditLog` with a durable implementation and periodically sign/store checkpoints outside the application database.
