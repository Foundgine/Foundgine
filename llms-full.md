# Foundgine 3.4.1 — Technical context
 
Foundgine is a configurable semantic and execution boundary for application capabilities. It separates application meaning, persistence mapping, authorization and execution from the interfaces and agents that consume them.
 
Applications configure their capabilities using language-idiomatic fluent APIs. Foundgine resolves operations against configured semantics, applies authorization at the execution boundary, and carries the authorized operation into provider execution. The same configured model can serve multiple interfaces and consumers.
 
Foundgine is designed for conventional application development first. It does not require AI, MCP, GraphQL or an agent framework. Those are optional consumers and integrations. It complements existing application architectures, ORM technologies and databases rather than replacing them.
 
## Product position
 
Fluent configuration is the primary experience in both C# and Java. Existing persistence and domain infrastructure can remain in place; adoption is incremental and capability-specific.
 
Foundgine is a semantic execution layer for .NET 10 and Java 27. It resolves structured caller intent into authorized, deterministic execution plans within an application-controlled boundary. Both implementations share the same architecture and vocabulary, expressed through language-idiomatic mechanisms.
 
## Semantic execution lifecycle
 
Retrieval is candidate discovery, not authorization.
 
1. **External actors and transports** — application code, MCP, GraphQL and JSON carry structured requests to the host-controlled boundary. Transport adapters do not own domain authority.
2. **Intent resolution** — the request is parsed and normalized into a typed intent and operation shape.
3. **Semantic model** — application metadata defines entities, fields, relationships, identities, aliases and exposed capabilities.
4. **Grounding and ambiguity resolution** — candidate discovery is evaluated against semantic evidence; unresolved ambiguity is rejected or returned for clarification rather than guessed.
5. **Authorization and capability boundaries** — trusted host context and policy determine which resolved operations the caller may exercise.
6. **Planning and Execution IR** — the authorized logical operation becomes a deterministic provider-independent plan and a constrained execution representation.
7. **Runtime and provider execution** — runtime orchestration invokes a conforming provider; providers execute the authorized artifact rather than reinterpret caller authority.
8. **Results and observability** — results, execution evidence, receipts and diagnostics are returned without leaking secrets or elevating provider output into authority.
## Core vocabulary
 
- **Semantic model** — application-defined meaning: entities, fields, identities, relationships, aliases and capabilities.
- **Intent** — what the caller requests, independently of physical SQL/provider operations.
- **Retrieval** — candidate discovery plus evidence; never an authorization decision.
- **Resolution** — selects and validates the intended semantic meaning.
- **Authorization** — determines what the current trusted execution context may exercise.
- **Plan** — provider-independent logical execution.
- **ExecutionIR** — the controlled intermediate execution representation at the provider boundary.
- **Provider** — physical execution implementation.
- **Evidence** — execution/security/plan context such as receipts and fingerprints.
## Adapters and providers
 
Intent can originate from application code, JSON, GraphQL, MCP or AI tools. These are adapters/consumers of the semantic boundary. GraphQL adapters exist in both implementations (Hot Chocolate for .NET and GraphQL Java for Java). JSON intent and MCP adapters exist in both .NET and Java.
 
Current providers include SQL/PostgreSQL and an InMemory provider, in both implementations. ORM execution is available through the EF Core adapter (.NET) and the JPA adapter (Java) over a shared ORM-neutral core. PostgreSQL retrieval includes relational lookup, pg_trgm, native full text, optional pg_search/BM25 and optional Apache AGE graph similarity, in both implementations.
 
## AI
 
AI may generate structured intent. It does not decide application authority, tenant identity, exposed semantics, credentials, policy or whether security invariants may be skipped.
 
## MCP
 
The MCP adapter (`Foundgine.Providers.Tools.MCP` in .NET, `com.foundgine.providers.tools.mcp` in Java) is a transport/capability adapter. Discovery is advisory. The host supplies trusted security context and every actual request is resolved and authorized.
 
## Security
 
The core security boundary: identity, tenant, audience, secrets and other authority remain host-owned. Foundgine does not infer authority from caller text, retrieval results, model output or capability discovery. Authorization is applied at the execution boundary.
 
## Evidence
 
The Foundgine.SupplyChain sample and AgentEndToEnd benchmark demonstrate an agent-facing business workload through MCP, semantic execution, authorization, ExecutionIR and PostgreSQL. The associated PenTest suite contains 14 deterministic cases: 7 MCP and 7 GraphQL. Benchmark pages separate measured performance from modeled efficiency estimates.
 
The Java core implements semantic lexical grounding with alias-weight evidence and a REQUIRES_CLARIFICATION outcome.
 
## Client SDKs
 
Three thin transport clients for a running Foundgine server are published as packages. They are not ports of the semantic runtime and hold no authority: identity, tenant, authorization, planning and providers stay on the .NET or Java server.
 
| SDK | Package | Import | Runtime |
|---|---|---|---|
| TypeScript | `@foundgine/typescript` (npm) | `import { FoundgineClient } from "@foundgine/typescript"` | Node.js 20+ or any runtime with `fetch` |
| Python | `foundgine` (PyPI) | `from foundgine import FoundgineClient` (also `FoundgineSyncClient`) | Python 3.11+, httpx |
| WebAssembly | `@foundgine/wasm` (npm) | `import { FoundgineClient, init } from "@foundgine/wasm"` | Node.js 20+, browsers, Workers, Deno; host supplies `fetch` |
 
**Protocols:** MCP over Streamable HTTP (default path `/mcp`, default protocol version 2025-06-18) and the Temporal workflow HTTP routes under `/foundgine/workflows` (processes, describe, start, status, decide, cancel, plus waitForCompletion, watch, startAndWatch).
 
**Main calls:** `listCapabilities()` / `list_capabilities()`, `execute(toolName, args)`, `notifications()`, `close()`, and the workflows client.
 
**Server MCP tools they call:** `foundgine_capabilities`, `foundgine_query` (open intent), `foundgine_query_semantic` (structured JSON intent), `foundgine_mutation_dry_run`, `foundgine_mutation_approve`, `foundgine_mutation_execute_approved`. Query tools take one `intentJson` string (rootEntity, selections, optional filter/order/limit/offset/after). Discovery via `foundgine_capabilities` is advisory and is not authorization.
 
## Packages
 
### .NET / NuGet
 
- **Foundgine.Core** — core contracts, metadata and semantic model.
- **Foundgine.Runtime** — application runtime, authorization and orchestration.
- **Foundgine.Providers** — SQL/in-memory, ORM and EF Core, Redis, pgvector, Elasticsearch, MCP/model integrations and generators.
- **Foundgine.Extensions** — GraphQL, Temporal and Keycloak extensions.
### Java / Maven
 
- **foundgine-core** — provider-independent semantic model, metadata, intent, planning and execution contracts.
- **foundgine-runtime** — application orchestration, authorization and mutation execution.
- **foundgine-providers** — SQL/PostgreSQL, InMemory, ORM/JPA, MCP, retrieval, gateway, event-store, outbox and Redis/shared-state providers.
- **foundgine-extensions** — GraphQL, Keycloak and Temporal extensions.
## Current release
 
3.4.1 · .NET 10 and Java 27
 
The fluent configuration API (introduced in 2.3.0) is part of the current model API (`FoundgineModel`) in both .NET and Java. It compiles to the same metadata registry as attributes/annotations and the AOT generator.

## Repository access for AI tools and crawlers

The repository is public, Apache-2.0 licensed and unrestricted for crawlers (`robots.txt`). No restructuring or build is needed to read any part of it.

- **Get everything:** `git clone https://github.com/Foundgine/Foundgine.git`, the [zip](https://github.com/Foundgine/Foundgine/archive/refs/heads/main.zip), or any single file at `https://raw.githubusercontent.com/Foundgine/Foundgine/main/<path>`.
- **Find anything:** `REPO-MAP.md` (directory map with descriptions and file counts) and `repo-index.json` (every file with its size; files of 1 MB or more carry `"large": true`). Regenerate both with `python tools/generate-repo-index.py`.
- **Agent rules:** `AGENTS.md` (build and test commands, architectural rules). `CLAUDE.md` and `.github/copilot-instructions.md` point to it.
- **Hosted indexes:** GitMCP `https://gitmcp.io/Foundgine/Foundgine`; DeepWiki `https://deepwiki.com/Foundgine/Foundgine` (MCP: `https://mcp.deepwiki.com/mcp`); Context7 `https://context7.com/foundgine/foundgine` (configured by `context7.json`). Steering notes for DeepWiki are in `.devin/wiki.json`. Ready-made client configs: `.mcp.json`, `.cursor/mcp.json`.
- **Details:** `docs/AI-ACCESS.md`.

## Public website reference

The [complete technical overview](https://foundgine.github.io/Foundgine/docs-site/foundgine-overview.html) is the long-form HTML companion to this context file. It provides a navigable reference for the architecture and implementation topics below. Use the repository documentation and source tree as the authority for exact implementation status and version-specific details.

### Website navigation

- [Website home](https://foundgine.github.io/Foundgine/docs-site/)
- [What is Foundgine?](https://foundgine.github.io/Foundgine/docs-site/what-is-foundgine.html)
- [Getting started](https://foundgine.github.io/Foundgine/docs-site/getting-started/)
- [Architecture](https://foundgine.github.io/Foundgine/docs-site/architecture/)
- [How it works](https://foundgine.github.io/Foundgine/docs-site/how-it-works/)
- [Walkthrough](https://foundgine.github.io/Foundgine/docs-site/walkthrough/)
- [Security](https://foundgine.github.io/Foundgine/docs-site/security/)
- [AI agents](https://foundgine.github.io/Foundgine/docs-site/ai-agents/)
- [Packages](https://foundgine.github.io/Foundgine/docs-site/packages/)
- [Samples](https://foundgine.github.io/Foundgine/docs-site/samples/)
- [Performance evidence](https://foundgine.github.io/Foundgine/docs-site/performance/)
- [Complete technical overview](https://foundgine.github.io/Foundgine/docs-site/foundgine-overview.html)

### Repository entry points

- [Root README](https://raw.githubusercontent.com/Foundgine/Foundgine/main/README.md)
- [Getting started](https://raw.githubusercontent.com/Foundgine/Foundgine/main/docs/GETTING-STARTED.md)
- [Architecture](https://raw.githubusercontent.com/Foundgine/Foundgine/main/docs/ARCHITECTURE.md)
- [Security](https://raw.githubusercontent.com/Foundgine/Foundgine/main/docs/SECURITY.md)
- [Testing](https://raw.githubusercontent.com/Foundgine/Foundgine/main/docs/TESTING.md)
- [Feature wiki](https://raw.githubusercontent.com/Foundgine/Foundgine/main/docs/wiki/index.md)
- [Repository map](https://raw.githubusercontent.com/Foundgine/Foundgine/main/REPO-MAP.md)
