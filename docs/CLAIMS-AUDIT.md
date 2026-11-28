# Claims audit

What the Foundgine website says, where it is enforced and how it is tested. Version 3.4.3. Claims last checked 27 November 2026 against 3.4.2.

> How this was checked: by reading the code paths listed here in the 3.4.2 source on 27 November 2026. The listed tests are part of the repository. Run them in your own pipeline for your build before relying on a claim in production.

## Every request goes through the same checks, whichever door it uses.

App code, HTTP, GraphQL, MCP, agent tools and workflow steps all end in the same engine, which limits the request, verifies the caller's warrant, authorizes it, plans it, re-checks authorization immediately before dispatch and only then calls the data provider. Mutations use the same policy through the mutation engine.

**Enforced in**

- [`src/csharp/Foundgine.Runtime/Execution/FoundgineEngine.cs`](../src/csharp/Foundgine.Runtime/Execution/FoundgineEngine.cs): ExecuteAsync: resource limits, warrant, authorization, planning, revalidation, provider
- [`src/csharp/Foundgine.Runtime/Execution/FoundgineMutationEngine.cs`](../src/csharp/Foundgine.Runtime/Execution/FoundgineMutationEngine.cs): AuthorizeAndPlan before ExecutePlanAsync
- [`src/csharp/Foundgine.Providers/Tools/MCP/FoundgineMcpTools.cs`](../src/csharp/Foundgine.Providers/Tools/MCP/FoundgineMcpTools.cs): MCP reads call IFoundgine.ExecuteAsync
- [`src/csharp/Foundgine.Providers/Tools/MCP/FoundgineMcpMutationTools.cs`](../src/csharp/Foundgine.Providers/Tools/MCP/FoundgineMcpMutationTools.cs): MCP writes call IFoundgineMutations
- [`src/csharp/Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineGraphQLQueryExecutor.cs`](../src/csharp/Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineGraphQLQueryExecutor.cs): GraphQL reads call IFoundgineExecutor
- [`src/csharp/Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineHotChocolateMutationExecutor.cs`](../src/csharp/Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineHotChocolateMutationExecutor.cs): GraphQL writes call IFoundgineMutations
- [`src/csharp/Foundgine.Providers/Tools/Http/FoundgineIntentEndpoints.cs`](../src/csharp/Foundgine.Providers/Tools/Http/FoundgineIntentEndpoints.cs): HTTP intent endpoints, 401 when unauthenticated
- [`src/csharp/Foundgine.Providers.AgentFramework/Tools/FoundgineAgentTools.cs`](../src/csharp/Foundgine.Providers.AgentFramework/Tools/FoundgineAgentTools.cs): agent tools use IFoundgine and IFoundgineMutations
- [`src/csharp/Foundgine.Extensions.Temporal/Activities/FoundgineOperationActivities.cs`](../src/csharp/Foundgine.Extensions.Temporal/Activities/FoundgineOperationActivities.cs): workflow steps get a single-use warrant per Foundgine call
- [`src/java/foundgine-runtime/src/main/java/com/foundgine/runtime/FoundgineEngine.java`](../src/java/foundgine-runtime/src/main/java/com/foundgine/runtime/FoundgineEngine.java): Java engine with the same pipeline

**Tests and evidence**

- [`src/csharp/tests/Foundgine.Security.Tests/Penetration`](../src/csharp/tests/Foundgine.Security.Tests/Penetration): deterministic penetration suite
- [`src/csharp/security/pentest/ATTACK-MATRIX.md`](../src/csharp/security/pentest/ATTACK-MATRIX.md): attack matrix SEC-01 to SEC-36
- [`src/csharp/tests/Foundgine.GraphQL.HotChocolate.Tests`](../src/csharp/tests/Foundgine.GraphQL.HotChocolate.Tests): GraphQL adapter tests
- [`src/csharp/tests/Foundgine.MCP.Tests`](../src/csharp/tests/Foundgine.MCP.Tests): MCP adapter tests

**Scope**

The checks apply to every request that reaches Foundgine. Code in your application that talks to your database directly, without calling Foundgine, is not covered. A workflow step is checked when it calls Foundgine, and you decide what the rest of the step does. Two live-environment items (network exposure and known-CVE scans, SEC-23 and SEC-24) are manual and need your own deployment.

## An AI asking is never the same as an AI being allowed.

The caller's identity, tenant, audience and warrant come from your host application, not from the AI. If the host supplies none, the request fails. The MCP tools tell the model never to supply identity, and the adapter replaces any security data in the intent with the host's.

**Enforced in**

- [`src/csharp/Foundgine.Core/Semantic/Security/Execution/SecurityExecutionContextProviderExtensions.cs`](../src/csharp/Foundgine.Core/Semantic/Security/Execution/SecurityExecutionContextProviderExtensions.cs): throws when the host supplies no security context
- [`src/csharp/Foundgine.Providers/Tools/MCP/FoundgineMcpTools.cs`](../src/csharp/Foundgine.Providers/Tools/MCP/FoundgineMcpTools.cs): intent is rebuilt with the host's Security
- [`src/csharp/Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineGraphQLQueryExecutor.cs`](../src/csharp/Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineGraphQLQueryExecutor.cs): host context replaces whatever the request carried
- [`src/csharp/Foundgine.Runtime/Hosting/IFoundgineIntentPrincipalProvider.cs`](../src/csharp/Foundgine.Runtime/Hosting/IFoundgineIntentPrincipalProvider.cs): your application owns the principal
- [`src/csharp/Foundgine.Providers/Tools/Http/HttpFoundgineIntentPrincipalProvider.cs`](../src/csharp/Foundgine.Providers/Tools/Http/HttpFoundgineIntentPrincipalProvider.cs): HTTP principal comes from your sign-in

**Tests and evidence**

- [`src/csharp/tests/Foundgine.Security.Tests/AdversarialAgentBoundaryTests.cs`](../src/csharp/tests/Foundgine.Security.Tests/AdversarialAgentBoundaryTests.cs): adversarial agent boundary
- [`src/csharp/security/pentest/ATTACK-MATRIX.md`](../src/csharp/security/pentest/ATTACK-MATRIX.md): SEC-01 capability escalation, SEC-17 MCP authority injection

**Scope**

Foundgine decides with the identity your application provides. If your application provides the wrong identity, Foundgine cannot know.

## One definition, the same result everywhere.

Every adapter turns its input into the same semantic request, which is checked against the one semantic model. The .NET and Java implementations are held to the same shared test vectors.

**Enforced in**

- [`src/csharp/Foundgine.Runtime/Api/IFoundgine.cs`](../src/csharp/Foundgine.Runtime/Api/IFoundgine.cs): one executor interface for every adapter
- [`src/csharp/Foundgine.Core/Semantic/Query/SemanticValueValidator.cs`](../src/csharp/Foundgine.Core/Semantic/Query/SemanticValueValidator.cs): value validation
- [`src/csharp/Foundgine.Core/Semantic/Query/SemanticFilterValidator.cs`](../src/csharp/Foundgine.Core/Semantic/Query/SemanticFilterValidator.cs): filter validation
- [`src/csharp/Foundgine.Core/Semantic/Query/SemanticQueryOptionsValidator.cs`](../src/csharp/Foundgine.Core/Semantic/Query/SemanticQueryOptionsValidator.cs): paging and ordering validation

**Tests and evidence**

- [`src/conformance/semantic-query-vectors.json`](../src/conformance/semantic-query-vectors.json): shared vectors
- [`src/csharp/tests/Foundgine.Semantics.Tests/SemanticQueryConformanceTests.cs`](../src/csharp/tests/Foundgine.Semantics.Tests/SemanticQueryConformanceTests.cs): .NET runs the vectors
- [`src/java/foundgine-core/src/test/java/com/foundgine/core/semantic/ir/SemanticQueryConformanceTest.java`](../src/java/foundgine-core/src/test/java/com/foundgine/core/semantic/ir/SemanticQueryConformanceTest.java): Java runs the same vectors
- [`src/conformance/tenant-first-gate-scenarios.json`](../src/conformance/tenant-first-gate-scenarios.json): shared tenant-first gate scenarios

**Scope**

Holds for the .NET 10 and Java implementations in this repository. The TypeScript, Python and WebAssembly SDKs are thin clients and contain no rules of their own.

## Keep what you already have.

Foundgine reaches a database or tool only through a provider at the last step. EF Core and JPA have providers, and your application keeps supplying who the caller is.

**Enforced in**

- [`src/csharp/Foundgine.Core/Execution/IExecutionProvider.cs`](../src/csharp/Foundgine.Core/Execution/IExecutionProvider.cs): the provider boundary
- [`src/csharp/Foundgine.Providers.EntityFrameworkCore/EfCoreProvider.cs`](../src/csharp/Foundgine.Providers.EntityFrameworkCore/EfCoreProvider.cs): EF Core provider
- [`src/java/foundgine-providers/src/main/java/com/foundgine/providers/storage/orm/jpa/JpaOrmProvider.java`](../src/java/foundgine-providers/src/main/java/com/foundgine/providers/storage/orm/jpa/JpaOrmProvider.java): JPA provider
- [`src/csharp/Foundgine.Providers/Storage/Sql/SqlExecutionProvider.cs`](../src/csharp/Foundgine.Providers/Storage/Sql/SqlExecutionProvider.cs): plain SQL provider
- [`src/csharp/Foundgine.Extensions.Keycloak`](../src/csharp/Foundgine.Extensions.Keycloak): Keycloak sign-in integration
- [`src/csharp/Foundgine.Extensions.Temporal`](../src/csharp/Foundgine.Extensions.Temporal): Temporal workflow integration

**Tests and evidence**

- [`src/csharp/tests/Foundgine.Orm.Tests`](../src/csharp/tests/Foundgine.Orm.Tests): ORM and EF Core provider tests
- [`src/csharp/tests/Foundgine.Sql.Tests`](../src/csharp/tests/Foundgine.Sql.Tests): SQL provider tests
- [`src/csharp/tests/Foundgine.E2E.Tests`](../src/csharp/tests/Foundgine.E2E.Tests): end-to-end tests

**Scope**

Keycloak is the sign-in integration that ships. Other sign-in systems connect by supplying the caller's security context. Temporal is the workflow integration that ships.

## Plug in your clients. The rules do not change.

MCP, GraphQL, HTTP and Temporal adapters ship for .NET and Java. TypeScript, Python and WebAssembly SDKs talk to a running Foundgine server and do not contain the runtime, so they cannot apply different rules.

**Enforced in**

- [`src/typescript/README.md`](../src/typescript/README.md): TypeScript client: MCP and workflow HTTP
- [`src/python/README.md`](../src/python/README.md): Python client: MCP and workflow HTTP
- [`src/wasm/README.md`](../src/wasm/README.md): WebAssembly client core
- [`src/java/foundgine-extensions/src/main/java/com/foundgine/extensions/graphql/FoundgineGraphQLQueryExecutor.java`](../src/java/foundgine-extensions/src/main/java/com/foundgine/extensions/graphql/FoundgineGraphQLQueryExecutor.java): Java GraphQL adapter, host security required
- [`src/java/foundgine-providers/src/main/java/com/foundgine/providers/tools/mcp/FoundgineMcpTools.java`](../src/java/foundgine-providers/src/main/java/com/foundgine/providers/tools/mcp/FoundgineMcpTools.java): Java MCP tools, host security required

**Tests and evidence**

- [`src/conformance/mcp-session-scenarios.json`](../src/conformance/mcp-session-scenarios.json): shared MCP session scenarios
- [`src/csharp/tests/Foundgine.MCP.Tests/McpSessionConformanceTests.cs`](../src/csharp/tests/Foundgine.MCP.Tests/McpSessionConformanceTests.cs): .NET runs them
- [`src/java/foundgine-providers/src/test/java/com/foundgine/providers/tools/mcp/McpSessionConformanceTest.java`](../src/java/foundgine-providers/src/test/java/com/foundgine/providers/tools/mcp/McpSessionConformanceTest.java): Java runs them

**Scope**

These are the adapters and SDKs that exist today. A technology with none of them needs a small adapter, which is the same pattern the existing ones follow.

## Is it worth it? Measure it.

The site promises no percentage of savings. It gives you a way to measure work done more than once, effort to change, security testing and running cost on one small project.

**Enforced in**

- [`docs/wiki/why-foundgine.md`](../docs/wiki/why-foundgine.md): business and architecture case
- [`docs/PRODUCT-POSITIONING-AND-COMMERCIAL-STRATEGY.md`](../docs/PRODUCT-POSITIONING-AND-COMMERCIAL-STRATEGY.md): commercial strategy and measurement model

**Scope**

Benchmark pages report what was measured in those runs. They are not a forecast for your system.

## Foundgine 3.4.3, .NET 10 and Java 27.

Version 3.4.3 is set in the .NET build, the Java build, and the TypeScript, Python and WebAssembly packages. .NET projects target net10.0 and the Java build compiles for release 27.

**Enforced in**

- [`Directory.Build.props`](../Directory.Build.props): VersionPrefix and net10.0
- [`src/java/pom.xml`](../src/java/pom.xml): release 27
- [`src/typescript/package.json`](../src/typescript/package.json): version
- [`src/python/pyproject.toml`](../src/python/pyproject.toml): version
- [`src/wasm/package.json`](../src/wasm/package.json): version
- [`CHANGELOG.md`](../CHANGELOG.md): 3.4.3 entry

**Scope**

Package pages tell you to confirm a version is published in NuGet or Maven Central before you pin it.

