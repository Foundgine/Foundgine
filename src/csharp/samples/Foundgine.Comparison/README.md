# Foundgine vs Hot Chocolate + EF Core

One small shop, one legacy PostgreSQL schema, two implementations of the same GraphQL API. The aim is not to show that Foundgine replaces EF Core or wins a line count. It shows a different way to describe, authorize and serve application data: **declare the model once, enforce policy in the query plan, add protocols without new data-access code.**

## Run

Needs Docker (Postgres only), the .NET 10 SDK and python3 (stdlib only).

```bash
./run.sh            # Postgres + Foundgine.Shop on :5001 (GraphQL and MCP)
./run.sh parity     # start both apps, diff their GraphQL answers, then smoke-test MCP
python3 tools/mint_token.py manager        # a token for your own requests (also: customer 1)
python3 tools/loc.py                       # reproduce the line counts below
```

`Foundgine.Shop` runs on :5001 and `HotChocolate.Shop` on :5002. Both read the same `db/init.sql` (tables `tbl_customer`, `tbl_order`, ... with columns such as `cust_id`, `qty`, `ord_total`).

## What is held constant

- Schema and seed data, the GraphQL query text, and the JWT block (HS256, same issuer, audience and claims).
- Access rules: `manager` reads everything. `customer` reads only their own Customer row and Orders (from the `customer_id` claim), may reach Products and OrderLines through those orders, and may not start a query at `orderLine`. A missing, invalid or expired token gets 401; a missing `tenant_id` is denied.
- Every case in `tools/parity.py` (12 cases, including the denials).

## The Foundgine side

`Domain/ShopModel.cs` is the whole data declaration, about 70 lines including comments. It states three things:

| Declaration | What it says | Example |
|---|---|---|
| `Semantic<T>` | Which models relate | `Order.Customer` with inverse `Customer.Orders`, `Order.Lines`, `OrderLine.Product` |
| `Persistence<T>` | Legacy table and column names | `Order -> tbl_order`, `Total -> ord_total` |
| `IdentityEntityMap<T>` | The class is its own storage type | one line per entity |

Everything else is convention:

- The key is the mapped `Id` member, so there is no `HasKey`.
- Foreign keys are found by name (`Order.CustomerId`, `OrderLine.OrderId`, `OrderLine.ProductId`), so there is no connection class. Use an explicit connection only when two models are related more than once or the key has another name.
- `Build()` validates the declaration at startup and lists every problem at once.

`Program.cs` is the composition root: JWT bearer, the EF provider, the JWT-to-warrant bridge, the engine, then GraphQL and (opt-in with `Mcp:Enabled=true`) MCP. `Auth.cs` holds the two small pieces of sample logic: the JWT-to-warrant provider and the row-level rule.

The row-level rule is one predicate per entity: Customer rows by `Id` and Order rows by `CustomerId` must equal the caller's `customer_id` claim, and it fails closed when the claim is missing.

## What the comparison shows

| Capability | Foundgine.Shop | HotChocolate.Shop |
|---|---|---|
| Nested selections | GraphQL adapter plus reusable endpoint and result assembly | Built-in |
| Filtering and sorting | Model-derived `where` and `order`, including collection filters and relationship traversal | `UseFiltering` / `UseSorting`; richer operators (`some/all/none`) |
| Cyclic selections | Supported by the query path | EF projections are kept acyclic per root (see `Mappings.cs`) |
| JWT bearer | Claims become a signed, short-lived warrant | Native ASP.NET integration |
| Row-level policy | One rule, enforced at every entity boundary including nested traversal | Root resolver scoping; nested fields rely on already-scoped roots |
| Second protocol (MCP) | `Mcp:Enabled=true`, no new mapping or policy code; checked by `tools/mcp_smoke.py` | A separate MCP surface would need its own resolvers and scoping |
| Pagination | `first` supported | Omitted so both apps share query text |

The baseline is not insecure: its resolver scoping is correct for this relationship graph. The difference is where the rule lives. In Foundgine a future relationship or a different caller cannot skip it, because it is part of plan execution rather than a convention in each resolver.

## Honest cost

| Measure (`tools/loc.py`) | Foundgine.Shop | HotChocolate.Shop |
|---|---:|---:|
| Non-blank, non-comment C# lines | 185 | 113 |

Foundgine has more sample code in this small app. Do not present this as a line-count win. The GraphQL endpoint and result assembly are shared library code, which moves ownership but is not free. Most of the difference is the JWT-to-warrant bridge and the in-memory warrant stores in `Program.cs` and `Auth.cs`, which Hot Chocolate gets from ASP.NET directly. Class declarations are written one per line on both sides, so the counts are comparable.

Other caveats:

- Hot Chocolate's built-in filtering is richer than the operators used here, and the dialects differ for collection predicates (`max(quantity) > 1` versus `some(quantity > 1)`); `parity.py` accounts for the equivalent cases.
- The EF provider here uses the model classes as the EF entities. This sample does not prove a separate storage class.
- Hot Chocolate projection is not enabled, so this is not a query-performance comparison.
- The warrant signing key and the replay and revocation stores are in memory, for this single-instance local sample only. Production needs a durable shared key and replay store.

## The point

1. Declare what entities mean, how they relate and how they map to existing storage, in one place.
2. Apply authorization as part of execution, not as a convention in each resolver.
3. Let each protocol (GraphQL here, MCP next to it) consume the same model and policy instead of reimplementing access rules.
