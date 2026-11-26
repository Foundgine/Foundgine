# Shared API adapters

The PostgreSQL-backed host exposes three transports over the same `SupplyChainService`:

| Transport | Endpoint | Role |
|---|---|---|
| MCP | `/mcp` | Existing agent/tool contract; retained for benchmark compatibility |
| REST | `/api/v1/supply-chain` | HTTP application API |
| GraphQL | `/graphql` | Hot Chocolate application API |

REST and GraphQL do not accept an actor as an operation argument. They resolve the actor from the pair `X-Foundgine-Actor` + `X-Foundgine-Token` and reject requests unless both validate. The actor header by itself is not an identity. Example demo credentials are defined in `OpenIntentDemoSecurity`; they are for local demonstration only and must be replaced by the host's real authentication/identity provider in production.

Example:

```bash
curl http://localhost:4422/api/v1/supply-chain/products \
  -H 'X-Foundgine-Actor: dave' \
  -H 'X-Foundgine-Token: dave-demo-token'
```

GraphQL uses the same credentials as HTTP headers. Example query:

```graphql
query {
  products
}
```

The GraphQL adapter serializes the service's provider-neutral payloads to `JsonElement` and exposes them through Hot Chocolate's `Any` scalar. This keeps anonymous CLR result shapes and EF Core entities out of the public schema. REST and GraphQL call the existing service methods, so capability authorization, customer ownership checks, correlation/error shaping, idempotency, repository transactions, ambiguity resolution and generic-entity admin restrictions remain in one place.

The existing MCP tool signatures are intentionally retained for benchmark-agent compatibility. In particular, the closed demo tools still carry an `actor` argument; this is a benchmark/demo contract, not a production identity design. A production MCP host should bind that actor from authenticated transport/session context and remove caller-supplied actor arguments. The Foundgine semantic MCP tools continue to use the host-established `SecurityExecutionContext` and warrant boundary.
