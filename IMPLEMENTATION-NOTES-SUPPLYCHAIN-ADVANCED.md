# SupplyChain Advanced API integration changes

## Scope

This update starts from the supplied `Foundgine (2).zip` repository and uses the supplied `UniversityApi.Foundgine.EFCore.Policies-Semantics.zip` as a reference for the shared application-service/transport-adapter pattern.

## Implemented

- Added REST endpoints under `/api/v1/supply-chain` to the PostgreSQL-backed benchmark host.
- Added Hot Chocolate GraphQL query and mutation resolvers at `/graphql`.
- REST and GraphQL delegate to the existing `SupplyChainService`; they do not introduce parallel business logic, EF entity exposure, raw `DbSet` access, or a second authorization implementation.
- REST and GraphQL resolve the demo actor only after validating both `X-Foundgine-Actor` and `X-Foundgine-Token`.
- Added a safe `JsonElement`/Hot Chocolate `Any` conversion boundary for the service's provider-neutral object payloads.
- Preserved the existing repository implementation, PostgreSQL-specific SQL, idempotency, order/inventory transactions, ranked supplier ambiguity resolution, generic entity operations, semantic pipeline, generated ORM maps, AOT metadata generation, and existing MCP workload contracts.
- Documented the adapter boundaries and the remaining production-hardening constraint for the legacy closed MCP tools.

## Deliberately not changed

- The deterministic in-memory semantic proving ground remains intact and independent of the PostgreSQL host.
- Semantic authorization and the application-level closed-capability authorizer remain separate. The sample's existing `AllowAllSemanticAuthorizationPolicy` is still a demo policy and must not be mistaken for production authorization.
- The legacy closed MCP tools retain their explicit actor argument for benchmark-agent compatibility. The new REST/GraphQL adapters do not repeat that design; production MCP should bind identity from authenticated transport/session context.
- No domain or persistence model was collapsed into a single EF-only model. Foundgine semantic metadata and persistence/ORM mapping remain separate concerns.

## Validation

- Project file XML parsed successfully.
- Source and endpoint registrations were statically inspected.
- A .NET build/test was not run: the execution environment does not contain the `dotnet` CLI. Run the benchmark solution build and the existing Advanced semantic test project after extracting this archive.

## Canonical open-intent protocol adapter refactor

This checkpoint also removes the Supply Chain-specific transport classes from `Semantic/Api/Mcp/` and replaces them with reusable integrations:

- Added `IFoundgineIntentHandler`, `FoundgineIntentRequest` and `FoundgineIntentResult` to `Foundgine.Runtime` (first placed in `Foundgine.Core`; see below). The contract receives the authenticated `ClaimsPrincipal`; identity is not part of the intent payload.
- Added `IFoundgineIntentPrincipalProvider` to Runtime so non-HTTP transports can obtain identity from their own authenticated session.
- Added `Foundgine.Providers.Tools.Http.FoundgineIntentEndpoints` and `AddFoundgineIntentHttpPrincipal()` for REST and ASP.NET-backed principal resolution.
- Added `Foundgine.Providers.Tools.MCP.FoundgineIntentMcpTools`. It uses the host-provided principal provider, so a non-HTTP MCP host can register its own implementation.
- Moved the reusable Hot Chocolate query field to `Foundgine.Extensions.GraphQL.HotChocolate.FoundgineIntentGraphQLResolver` and added the Hot Chocolate server dependency to the bundled `Foundgine.Extensions` package.
- Kept `SupplyChainIntentService`, `IntentCaller`, policies and the new `SupplyChainIntentHandler` in the Supply Chain application. The handler is the only application-specific bridge shared by REST, GraphQL and MCP.
- Added a Development-only header-to-claims middleware for the demo host. Production identity must come from the host's normal authentication middleware.
- Updated endpoint, GraphQL and MCP integration tests to exercise the shared handler and preserve fail-closed identity behavior.

The three removed sample files (`IntentEndpoint.cs`, `IntentMcpTools.cs`, and `IntentGraphQlQuery.cs`) were relocated/replaced by package-level adapters rather than discarded. The GraphQL adapter is in `Foundgine.Extensions.GraphQL`; REST and MCP adapters are in `Foundgine.Providers`.

Additional static checks for this refactor: project XML parsed, project references resolved, C# delimiter checks passed, and all original archive files are retained except those three relocated sample adapter files. A .NET build/test was not run because `dotnet` is not installed in this environment.

## Contract placement refactor

- Moved the intent handler, request, operation and result types from `Foundgine.Core` to `Foundgine.Runtime` (`Api/`), and the principal provider to `Foundgine.Runtime` (`Hosting/`). `Core` keeps `ReadIntent` and its compiler.
- Replaced `FoundgineIntentResult.StatusCode` with `FoundgineIntentOutcome`. The REST adapter maps outcomes to HTTP statuses; `SupplyChainIntentHandler` maps the sample service's classification to outcomes.
- Added `Http_adapter_owns_the_mapping_from_outcome_to_status`.

## Compile and test follow-up

Fixed:

- REST adapter: unknown paths under `/intent` return a JSON `not_found` again, and a wrong verb returns a JSON `method_not_allowed` with `Allow`. The unknown-path regression failed `Http_endpoint_takes_the_caller_from_headers_only`.
- REST adapter: an exception escaping the handler is logged and returned as a JSON `internal_error`.
- MCP adapter: `mode` of null or blank means `execute` again. Exception text from the handler is no longer returned to the client.
- GraphQL adapter: uses web JSON defaults, like REST and MCP; null or blank `mode` means `execute`.
- `AddFoundgineIntentHttpPrincipal()` keeps a principal provider the host already registered.
- `GraphQlIntentAdapterTests.cs` was missing `using Xunit;`.
- Documented in `docker-compose.yml` that the container runs as Production, so the demo header bridge is off.

Verification used .NET SDK 10.0.115 without NuGet access:

- Built with the real SDK: `Foundgine.Core` and `Foundgine.Runtime` (0 errors, 0 warnings; in a scratch copy, Runtime's DI abstractions package was replaced by the ASP.NET Core framework reference). The in-memory provider sources, REST adapter and sample application sources compiled together in a scratch project.
- Ran with a minimal xUnit stand-in: 16 repository tests from `CanonicalIntentEndpointTests` and `GraphQlIntentAdapterTests` passed, including REST over Kestrel.
- MCP and GraphQL adapter sources were compiled and exercised against stand-in SDK types. This does not verify the real MCP SDK or Hot Chocolate APIs.
- Not run: `Graphql_resolver_uses_the_same_application_handler_and_authenticated_principal`, `Graphql_http_field_uses_the_same_open_intent_handler`, `Mcp_transport_takes_the_caller_from_headers_and_never_from_arguments`, the other test files, the `Api/Mcp` host and the NuGet packaging projects.

To finish verification where NuGet is available:

```
dotnet test src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Tests
dotnet build src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Api/Mcp
```
