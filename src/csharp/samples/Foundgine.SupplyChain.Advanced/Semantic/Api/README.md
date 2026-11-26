# API layer

The HTTP/GraphQL/MCP host is in `Semantic/Api/Mcp/`. It exposes the same Supply Chain application intent handler through three transport adapters:

- REST: `POST /intent`, `POST /intent/explain`, `GET /intent/capabilities`
- GraphQL: `POST /graphql`, field `foundgineIntent(intent:, mode:)`
- MCP: `POST /mcp`, tool `foundgine_intent`

The reusable protocol implementations live outside the sample:

| Adapter | Package/source | Application-specific code kept in the sample |
| --- | --- | --- |
| ASP.NET Core REST | `Foundgine.Providers.Tools.Http.FoundgineIntentEndpoints` | `SupplyChainIntentHandler`, caller mapping and semantic policies |
| GraphQL | `Foundgine.Extensions.GraphQL.HotChocolate.FoundgineIntentGraphQLResolver` | `SupplyChainIntentHandler`, caller mapping and semantic policies |
| MCP | `Foundgine.Providers.Tools.MCP.FoundgineIntentMcpTools` | `SupplyChainIntentHandler`, caller mapping and semantic policies |

All three adapters depend on `IFoundgineIntentHandler`. They do not know the Supply Chain domain, the caller's tenant/role model, or how semantic authorization is implemented. GraphQL and MCP obtain their principal through the host-owned IFoundgineIntentPrincipalProvider; non-HTTP hosts can supply a different implementation. REST passes HttpContext.User directly. The handler receives the host-authenticated `ClaimsPrincipal`; identity is never accepted as part of the intent.

The sample's `SupplyChainIntentDemoIdentityMiddleware` is enabled only in Development and translates the `X-Foundgine-Tenant` and `X-Foundgine-Role` demonstration headers into claims. Production hosts must use their normal authentication middleware instead.

## MCP authorization lab

The separate Supply Chain MCP lab tools (`read_entity`, `write_entity`, `policy_probe`) remain sample-specific because they demonstrate the Supply Chain authorization policy and claim-validation behavior. They are not part of the generic `foundgine_intent` transport adapter.


## Order mutations

The same host also exposes a typed Supply Chain mutation boundary:

- MCP: `foundgine_mutation` (`POST /mcp`)
- GraphQL: `foundgineMutation(mutation:, correlationId:)` (`POST /graphql`)

Both adapters delegate to `SupplyChainMutationDispatcher`. The host registers that dispatcher once so approval handles, idempotency state, and audit records are shared across transports. The dispatcher accepts `place_order` and `cancel_order` with `dry_run`, `approve`, `execute`, and `execute_approved` lifecycle modes. Caller identity and tenant are derived from the authenticated principal, never from mutation JSON. See [`../../CROSS-LANGUAGE-SCOPE.md`](../../CROSS-LANGUAGE-SCOPE.md) for lifecycle guarantees and the in-memory sample-store limitation.
