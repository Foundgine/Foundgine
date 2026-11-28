# Foundgine Supply Chain — Advanced Sample (Java)

The Java counterpart to
[`Foundgine.SupplyChain.Advanced`](../../../csharp/samples/Foundgine.SupplyChain.Advanced/README.md).
It is the Java Advanced sample for the fluent-configured semantic model →
tenant/role authorization → Foundgine's mutation engine → shared HTTP, GraphQL and MCP intent
transports → a PostgreSQL-backed high-assurance `place_order` / `cancel_order` boundary.

> **New here?** This README covers _how to run it_. For the file-by-file "why" — mirroring the C#
> sample's numbered `docs/` set — see
> [`SupplyChain-Advanced-Tutorial.md`](./SupplyChain-Advanced-Tutorial.md).

> **Status (3.4.2 source):** the most recent reported Maven run failed in the Advanced
> sample test phase. Fixes for semantic discovery, physical-column expectations and the catalog
> count are in the source but have not yet been re-run. Treat this sample as
> implemented-unverified until the Java 27 Maven and PostgreSQL gates pass.

## Flow

```
HTTP / GraphQL / MCP caller
        │  /intent · /graphql · /mcp
        ▼
  AdvancedIntentEndpoint / AdvancedGraphQLIntentEndpoint
        │
        ▼
  IFoundgineIntentHandler<IntentCaller> (runtime contract)
        │
        ▼
  SupplyChainRuntimeIntentHandler → SupplyChainIntentService
        │
        ├── Read intent: resolve → authorize → plan → execute → evidence
        └── Mutation intent: dry-run / approve / execute / execute-approved
                     ↳ FoundgineMutationEngine → ExecutionIR → provider
                     ↳ SupplyChainAuthorization (tenant + role policy)
        │            ↳ SupplyChainSemanticModel (entities, relationships, weighted aliases)
        ▼
  PlaceOrderService / CancelOrderService — or —
  PostgresSupplyChainStore (real transactions, row locks, idempotency)
```

`Main` is the in-memory smoke-test entry point over the `SupplyChainData` fixture: it exercises a
claims-based scope reduction, recursive supplier-risk / fulfillment scenarios, order placement and
cancellation with idempotency-key replay. Run it to execute that path; the latest Maven verification
status is recorded above.

## What this demonstrates, package by package

| Package         | C# equivalent                                                   | Role                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| --------------- | --------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `model`         | fluent configuration                                            | `SupplyChainFoundgineModel` registers the semantic, persistence, entity-map and connection configurations that produce the metadata registry. `SupplyChainModelConnections` adds a model-to-entity connection (`PurchaseOrder` to `Supplier`) with a field map and a same-tenant authorization predicate; a type conversion is a third argument to `maps(...)`, e.g. `.maps(Contract::code, ContractRow::codeNumber, PARSE)`. |
| `domain`        | `Semantic/Domain`                                               | The application model as annotation-free `record`s. |
| `semantics`     | `Semantic/Semantics`                                            | `SupplyChainSemanticModel` builds the Foundgine `SemanticModel` from the domain, including weighted alias evidence (`Vendor`/`Seller` for `Supplier`, `PO`/`POs`/`Buy`/`Buys` for `PurchaseOrder`) and the `PlaceOrderCommand`/`CancelOrderCommand` mutation entities.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| `authorization` | `Semantic/Authorization`                                        | `SupplyChainAuthorization` wires tenant/role rules into a `ConfiguredSemanticAuthorizationPolicy` — entity, field, relationship, predicate, and named-operation rules. `Claims` validates untrusted client-supplied claims (scope, warehouse, max rows, change ticket, expiry) and rejects reserved/hostile keys outright.                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `mutation`      | `Semantic/Application` + `mcp/AdvancedMcpServer.java`                        | `AdvancedMutationPipeline` drives Foundgine's `FoundgineMutationEngine` end to end (intent → plan → authorization → ExecutionIR → provider). `PlaceOrderService`/`CancelOrderService` are the in-memory high-assurance domain services; `AdvancedMcpFacade` builds typed semantic commands and dispatches them through the shared runtime handler for HTTP, GraphQL and MCP.                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| `postgres`      | `mcp/AdvancedMcpServer.java` (SQL boundary)                                  | `PostgresSupplyChainStore` owns the physical transaction, advisory locks, server-side pricing, and exact inventory-lot allocation for `place_order`/`cancel_order` against real PostgreSQL.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `mcp`           | `mcp/AdvancedMcpServer.java` + `Semantic/McpClient` | `AdvancedMcpServer` exposes JSON-RPC-over-HTTP MCP (`/mcp`) with `foundgine_query`, `foundgine_mutation` and `foundgine_intent`. The MCP transport uses JDK `HttpServer` and Jackson; the combined sample server also mounts the GraphQL intent adapter using `graphql-java`. `AdvancedMcpClientDemo` is a raw-HTTP MCP client; no MCP SDK is required. |
| `graphql`      | `graphql/AdvancedGraphQLIntentEndpoint.java` | Focused GraphQL intent endpoint (`POST /graphql`) exposing `foundgineIntent` and `foundgineMutation`; both delegate to the same runtime handler as HTTP and MCP. Distinct from the schema-derived GraphQL adapter in `foundgine-extensions`, demonstrated by `foundgine-shop`. |
| `intent`        | `intent/` + runtime contract | `SupplyChainRuntimeIntentHandler` implements `IFoundgineIntentHandler<IntentCaller>` for reads and semantic writes. HTTP (`/intent`, `/intent/mutation`), GraphQL (`foundgineIntent`, `foundgineMutation`) and MCP (`foundgine_intent`, `foundgine_mutation`) share the same handler. `IntentCaller` is host-supplied and never part of the intent. |
| `candidate-search demo` | ``mcp/AdvancedMcpServer.java`` (`find_top_supplier_overdue_orders`) | `TopSupplierOverdueOrdersService` is a direct PostgreSQL candidate-search demo: it ranks exact, `pg_trgm`, full-text and optional `pg_search` matches and returns `resolved` / `clarification_needed` / `not_found`. It is hand-registered as a fourth MCP tool and queries PostgreSQL directly; it is **not** wired through the Java core `SemanticLexicalResolver` or its `GroundingDecision` pipeline. Its `plan` field is a stable hash of the fixed Supplier read shape, not a full `ExecutionIRCompiler`-derived fingerprint — see the class Javadoc. |
| `scenarios`     | `Semantic/Tests` (adversarial cases)                            | `Scenarios`/`ScenariosAdversarialInvariants` — recursive BOM/supplier-risk traversal with cycle detection, and fulfillment-shortage calculation, run against the live semantic model and authorization context rather than a disconnected fixture.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |

## Fluent configuration reference

The complete Java builder inventory, from convention/defaults through semantic metadata, persistence DDL metadata, entity maps, physical relationships and authorized model connections, is in [CONFIGURATION-COOKBOOK.md](./CONFIGURATION-COOKBOOK.md). It also records the current Java/C# API differences and the protocol coverage boundary.

## Run

Start PostgreSQL:

```bash
cd src/java/samples/foundgine-supply-chain-advanced
docker compose up -d postgres
```

Compile and run the in-memory smoke test (`Main`):

```bash
cd src/java
mvn -pl samples/foundgine-supply-chain-advanced -am compile exec:java \
  -Dexec.mainClass=com.foundgine.samples.supplychain.advanced.Main
```

Run the MCP server:

```bash
mvn -pl samples/foundgine-supply-chain-advanced -am compile exec:java \
  -Dexec.mainClass=com.foundgine.samples.supplychain.advanced.mcp.AdvancedMcpServer
```

The server listens on `http://localhost:4783/mcp` (health check at `/health`). In a second
terminal, run the client demo against it:

```bash
mvn -pl samples/foundgine-supply-chain-advanced -am compile exec:java \
  -Dexec.mainClass=com.foundgine.samples.supplychain.advanced.mcp.AdvancedMcpClientDemo
```

## Actors and authorization

Authorization is tenant- and role-scoped (`CUSTOMER`, `ANALYST`, `WAREHOUSE_OPERATOR`,
`SUPPLY_CHAIN_MANAGER`) rather than the C# sample's five named actors (Alice/Bob/Carol/Dave/Admin),
but it enforces the same principles: entity/field/relationship/predicate rules run before planning,
`Supplier.RiskScore` and `InventoryLot.Quarantined` are restricted to roles that need them, and
client-supplied claims are validated and can only ever _narrow_ an identity's access
(`Authorization.applyClaims`), never expand it — a claim trying to smuggle in `role` or `isAdmin` is
rejected as a spoofing attempt before it reaches the policy at all.

## High-assurance mutation

`place_order` and `cancel_order` implement the same guarantees as the C# `PlaceOrder`/`CancelOrder`:
actor/tenant/ownership checks, positive-quantity validation, inventory availability across
authorized warehouses, server-side price calculation, atomic order + line + inventory-lot updates,
and idempotency-key replay protection — implemented in both the in-memory
(`PlaceOrderService`/`CancelOrderService`, used by `Main` and the MCP server) and PostgreSQL
(`PostgresSupplyChainStore`) paths. The PostgreSQL store additionally takes a
`pg_advisory_xact_lock` on the idempotency key so concurrent replays of the same key serialize
instead of racing. Runtime verification remains pending as noted above.

## Canonical intent endpoint and mutation boundary

One endpoint takes the canonical read intent itself, not an endpoint per query. The intent names
*what is wanted* in the semantic vocabulary (entity, field and relationship names or aliases). Foundgine
does the rest in a real engine: name and path resolution, authorization, planning, execution, receipt.
The caller (tenant and role) is supplied by the host and is never part of the intent.

The same `IFoundgineIntentHandler<IntentCaller>` also handles semantic mutations. The Advanced
sample exposes `POST /intent/mutation`, GraphQL `foundgineMutation`, and MCP `foundgine_mutation`.
Mutation JSON identifies a semantic command (`place_order` or `cancel_order`) and supports
`mode: execute` (default), `dry_run`, `approve`, and `execute_approved`. The approver identity is
derived from the host-supplied caller, not from JSON. `approve` returns an opaque, one-time
`approvalId`; `execute_approved` accepts only that handle. The demo keeps approvals in a bounded,
ten-minute in-memory store bound to tenant and role. Production applications should replace that
store with durable, distributed approval storage and audit/revocation controls.

```bash
mvn -q -pl samples/foundgine-supply-chain-advanced -am exec:java \
  -Dexec.mainClass=com.foundgine.samples.supplychain.advanced.mcp.AdvancedMcpServer

# 1. Same intent, two roles: the analyst sees RiskScore, the operator does not (it is pruned, not an error)
INTENT='{"rootEntity":"Supplier","selections":[{"field":"Name"},{"field":"RiskScore"}]}'
curl -s localhost:4783/intent -H 'X-Foundgine-Role: analyst'            -d "$INTENT"
curl -s localhost:4783/intent -H 'X-Foundgine-Role: warehouse_operator' -d "$INTENT"

# 2. Tenant isolation comes from the policy, not the intent
curl -s localhost:4783/intent -H 'X-Foundgine-Role: analyst' -H 'X-Foundgine-Tenant: tenant-b' \
  -d '{"rootEntity":"Supplier","selections":[{"field":"Name"}]}'

# 3. Mutation lifecycle: dry-run / approve / execute-approved (approval IDs are one-time)
curl -s localhost:4783/intent/mutation -H 'X-Foundgine-Role: supply_chain_manager' \
  -d '{"tool":"place_order","mode":"dry_run","customerId":1,"productId":4,"quantity":1,"idempotencyKey":"demo-place-001"}'
curl -s localhost:4783/intent/mutation -H 'X-Foundgine-Role: supply_chain_manager' \
  -d '{"tool":"place_order","mode":"approve","customerId":1,"productId":4,"quantity":1,"idempotencyKey":"demo-place-001"}'
# Copy the returned approvalId into the next request; it is bound to the tenant/role and expires in ten minutes.
# Successful execution returns a provider-neutral receipt with plan/authorization/result fingerprints,
# affected operation nodes, effects, provider identity and approval metadata.
curl -s localhost:4783/intent/mutation -H 'X-Foundgine-Role: supply_chain_manager' \
  -d '{"mode":"execute_approved","approvalId":"<approvalId-from-previous-response>"}'

# 4. A relationship name resolves to the declared path (Product > PurchaseOrderLine > PurchaseOrder > Shipment)
curl -s localhost:4783/intent/explain -H 'X-Foundgine-Role: analyst' \
  -d '{"rootEntity":"Product","selections":[{"field":"Name"},{"relationship":"shipments","children":[{"field":"Status"}]}]}'

# 4. What may this caller do?
curl -s localhost:4783/intent/capabilities -H 'X-Foundgine-Role: customer'
```

GraphQL uses the same canonical read intent and host-owned caller context:

```graphql
query RunIntent($intent: String!) {
  foundgineIntent(mode: "execute", intentJson: $intent) {
    outcome
    data
    errorCode
    errorMessage
  }
}
```

Send this query to `POST /graphql` with a JSON `variables` object containing the `intent` string. The
GraphQL `data` field is JSON text so the intent result shape remains transport-neutral without
introducing a second GraphQL-specific domain model. This focused endpoint is not the schema-derived
entity GraphQL adapter; the latter remains available through `foundgine-extensions` and the
`foundgine-shop` comparison sample. Mutations use the corresponding GraphQL mutation field:

```graphql
mutation PlaceOrder($mutation: String!) {
  foundgineMutation(mutationJson: $mutation) {
    outcome
    data
    errorCode
    errorMessage
  }
}
```

Pass the same mutation JSON string shown for `/intent/mutation` as the `mutation` variable. The
`data` field contains JSON text for the mutation result or approval receipt.

| Path | Method | Does |
| --- | --- | --- |
| `/intent` | POST | Executes the intent as the caller; returns rows keyed `Entity.Field`, evidence and receipt. |
| `/intent/explain` | POST | Dry run: how names resolved, which requested fields the caller's policy removed (`prunedFields`), the authorized plan. |
| `/intent/capabilities` | GET | Per entity: read access (`ALLOWED`/`CONDITIONAL`/`DENIED`), visible fields and relationships, aliases, intent shape. |
| `/intent/mutation` | POST | `{"tool":"place_order"|"cancel_order", "mode":"execute\|dry_run\|approve", ...}`; `execute_approved` accepts only the opaque `approvalId`. |
| `/mcp` tool `foundgine_intent` | POST | `{"intent": {...}, "mode": "execute\|explain\|capabilities"}`, same service and header-based caller. |
| `/mcp` tool `foundgine_mutation` | POST | `mutationJson` carries the same command and lifecycle mode as `/intent/mutation`. |
| `/graphql` field `foundgineIntent` | POST | GraphQL wrapper around the same runtime read request; `data` is JSON-encoded result content. |
| `/graphql` field `foundgineMutation` | POST | GraphQL wrapper around the same runtime mutation handler and approval lifecycle. |

HTTP errors are `{"error":{"type","message"}}`: `invalid_intent` (400, including unknown names and any
identity-shaped property such as `tenant`, `role` or `approvedBy` in either intent body), `forbidden` (403),
`payload_too_large` (413, limit 64 KiB), `method_not_allowed` (405), `timeout` (504), and
`approval_invalid` (400 for expired, replayed or unknown approval handles). The GraphQL adapter returns
`FoundgineIntentOutcome` in the GraphQL field; MCP maps the same failure classification to a tool error.

DEMO ONLY: `X-Foundgine-Tenant` and `X-Foundgine-Role` are plain headers so the sample can show one intent
behaving differently per role. A real host derives identity from an authenticated principal. Unknown
tenants and roles are refused, the default is the least privileged role, and every response echoes the
effective caller. Reads run on a snapshot of the in-memory fixture taken at startup, so orders placed
through the mutation tools are not visible to intents. Tests: `CanonicalIntentEndpointTest`.

## Where to go next

- [Step-by-step tutorial](./SupplyChain-Advanced-Tutorial.md) — build every piece above from
  scratch, in order.
- [`../foundgine-supply-chain`](../foundgine-supply-chain/README.md) — the smaller starter this
  sample builds on conceptually.
- [C# `Foundgine.SupplyChain.Advanced`](../../../csharp/samples/Foundgine.SupplyChain.Advanced/README.md)
  — the cross-language reference. Java already carries weighted entity, field and relationship
  aliases and the `AliasWeightEvidenceGate` (see `SupplyChainAliasWeightParityTest`). It also has
  the `find_top_supplier_overdue_orders` demonstration, but that Java capability currently uses a
  direct PostgreSQL candidate-search service; it is not yet integrated with the core
  `SemanticLexicalResolver` / `GroundingDecision` pipeline used by the C# implementation.

## Fluent ORM path (Step 72 — implemented-unverified)

The advanced JPA path runs on `FoundgineOrm` (`jpa/AdvancedJpaOrm`). The semantic metadata comes from the fluent
`SupplyChainFoundgineModel`; the annotation processor (configured in this module's `pom.xml`) generates
`GeneratedOrmMaps` and `GeneratedJpaModels` from the `@FoundgineOrmEntity` persistence classes (`WarehouseEntity`,
`InventoryLotEntity`, `ProductEntity`) into `com.foundgine.samples.supplychain.advanced.generated`.

### Method-reference reads

```java
var reader = new AdvancedReader(orm.reader(em), caller);
var rows = reader.from(Warehouse.class).select(Warehouse::id, Warehouse::name).rows();
```

Only getter/record-accessor method references are accepted (Java lambdas expose no expression trees); every read still
goes through authorize -> plan -> ExecutionIR -> JPA.

### CRUD

`application/AdvancedCrud` gives create/read/update/delete for Warehouse, InventoryLot and Product through the generic
`JpaEntities` gateway. Reads are gated by Foundgine. Writes need `Authorization.canWrite` plus scope rules: warehouses
are stamped with the caller's tenant and never change tenant; lots are checked through their (allowed, tenant-visible)
warehouse and are never listed without one; products are shared catalogue data. Known limit: `AdvancedJpaPolicy` still
lets a root read of `InventoryLot` through (it has no tenant column), so use `AdvancedCrud.listLots` rather than raw
lot reads.

### Migrations and schema ownership

The Advanced sample still uses the handwritten Flyway baseline
(`src/main/resources/db/migration/V1__supply_chain_advanced.sql`) and repeatable demo data
(`src/main/resources/db/demo/R__demo_data.sql`). The new `foundgine-migrations` generator is
available in this reactor, but the Advanced fluent model is **not yet a safe schema source**:
its configured persistence tables and semantic entities do not describe the physical PostgreSQL
schema consistently. Generating a migration from it now would create a second, incorrect schema.

The first reconciliation step now maps the three JPA-backed entities (`Product`, `Warehouse`,
`InventoryLot`) to their exact V1 column names, SQL types, required-column semantics and existing
indexes. `AdvancedSchemaModelParityTest` checks this subset through `SchemaBuilder`. This is not full
V1 coverage: customer/order transaction tables, idempotency tables, supplier/purchase-order tables,
foreign keys, CHECK constraints, defaults, identity behavior and the `pg_trgm` extension remain
outside the generated model. See [Advanced schema audit and migration plan](./ADVANCED-SCHEMA-AUDIT.md)
for the exact boundary and next steps.

`AdvancedDatabase` continues to apply Flyway migrations on start
(`SUPPLYCHAIN_MIGRATE_ON_START`, default true; demo rows with `SUPPLYCHAIN_SEED_DEMO`, default
true). Hibernate remains `hbm2ddl.auto=validate`. Do not replace or baseline the existing V1
until the fluent model, JPA entities, direct PostgreSQL SQL, and constraints have a single agreed
physical schema.


### Schema reconciliation status

The Java fluent model now includes explicit physical persistence rows for the transactional V1 tables and the ambiguity-resolution tables `suppliers` and `purchase_orders`. Their column names, SQL types, nullability, keys and supported indexes are checked by `AdvancedSchemaModelParityTest`. The PostgreSQL `pg_trgm` GIN/operator-class index, identity generation, foreign keys, CHECK constraints and defaults remain owned by the unchanged handwritten V1. Generated migrations are not yet enabled for this sample; see [`ADVANCED-SCHEMA-AUDIT.md`](ADVANCED-SCHEMA-AUDIT.md).


## Shared runtime intent contract

The runtime provides one `IFoundgineIntentHandler<TPrincipal>` for read and mutation intent
operations. `FoundgineIntentRequest` carries a typed `ReadIntent`; `FoundgineMutationIntentRequest`
wraps the existing `SemanticMutationRequest` and exposes `DRY_RUN`, `APPROVE`, `EXECUTE` and
`EXECUTE_APPROVED`. The host supplies its authenticated principal separately. The handler derives
approver identity from that principal, and the sample uses opaque, one-time approval handles rather
than exposing `MutationPlanApproval` over the wire. The generic principal type avoids coupling the
runtime to a specific Java security framework. `FoundgineIntentResult` and `FoundgineIntentOutcome`
keep transport-specific response types outside the runtime. See the [configuration cookbook](./CONFIGURATION-COOKBOOK.md#runtime-intent-boundary-one-handler-for-every-protocol).


### Mutation audit evidence

The Java mutation engine can be supplied an `IAuditLog`; the advanced runtime should use a durable
`AuditEventStore` implementation and may wrap it with `HashChainedAuditLog`. Audit entries correlate
approval, denial, provider failure and successful mutation events with the execution receipt ID.
The hash chain is verifiable with `HashChainedAuditLog.verify(...)` over the complete event stream.
It is tamper-evident, not a cryptographic signature: production deployments must protect the audit
store and chain head, and should use immutable storage or externally protected signing keys for
stronger administrator-resistant evidence. If success-event persistence fails after provider commit,
`MutationAuditPersistenceException` carries the completed receipt; reconcile rather than retry.


### Request correlation across protocols

The HTTP, GraphQL and MCP adapters accept an optional `X-Correlation-ID` header (1-128 characters from `[A-Za-z0-9._:-]`). If absent, the adapter generates one. The value is propagated as execution context metadata and is returned separately from the unique `receipt.requestId` as `receipt.correlationId`. It is for tracing only and never affects caller identity, tenant selection, authorization or idempotency.


The runtime also exposes `IAuditCheckpointSigner` so production hosts can sign audit-chain head checkpoints using an external key service. The sample does not configure a production signer or claim administrator-proof audit storage.


### Sample audit stream

The Advanced sample shares one `HashChainedAuditLog` across read execution, mutation execution and the opaque-approval boundary. It records read success/failure/denial, mutation dry-run, approval, success/failure/denial, and facade-level invalid, cross-tenant and replayed approval attempts. The sample store is in-memory: it demonstrates event correlation and chain verification, not durable production retention. Hosts can replace the sink with a durable `IAuditLog` and externally anchor checkpoints with `IAuditCheckpointSigner`.
