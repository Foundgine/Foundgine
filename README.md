<picture>
  <source media="(prefers-color-scheme: dark)" src="docs-site/assets/logo/foundgine-logo.svg">
  <img src="docs-site/assets/logo/foundgine-logo.svg" alt="Foundgine" width="360">
</picture>

[![NuGet Version](https://img.shields.io/nuget/v/Foundgine.Core?label=NuGet%20Version)](https://www.nuget.org/packages/Foundgine.Core/)
[![Unit Tests](https://img.shields.io/github/actions/workflow/status/Foundgine/Foundgine/csharp-build.yml?branch=main&job=unit-tests&label=Unit%20Tests)](https://github.com/Foundgine/Foundgine/actions/workflows/csharp-build.yml)
[![Integration Tests](https://img.shields.io/github/actions/workflow/status/Foundgine/Foundgine/csharp-build.yml?branch=main&job=integration-tests&label=Integration%20Tests)](https://github.com/Foundgine/Foundgine/actions/workflows/csharp-build.yml)
[![Penetration Tests](https://img.shields.io/github/actions/workflow/status/Foundgine/Foundgine/csharp-build.yml?branch=main&job=security-penetration-gate&label=Penetration%20Tests)](https://github.com/Foundgine/Foundgine/actions/workflows/csharp-build.yml)

[![Maven Central Version](https://img.shields.io/maven-central/v/io.github.cristianbarragan/foundgine-core?label=Maven%20Central)](https://central.sonatype.com/artifact/io.github.cristianbarragan/foundgine-core)
[![Java Unit Tests](https://img.shields.io/github/actions/workflow/status/Foundgine/Foundgine/java-build.yml?branch=main&job=java-build&label=Java%20Unit%20Tests)](https://github.com/Foundgine/Foundgine/actions/workflows/java-build.yml)
[![Java Integration Tests](https://img.shields.io/github/actions/workflow/status/Foundgine/Foundgine/java-build.yml?branch=main&job=java-postgres-e2e&label=Java%20Integration%20Tests)](https://github.com/Foundgine/Foundgine/actions/workflows/java-build.yml)
[![Java Penetration Tests](https://img.shields.io/github/actions/workflow/status/Foundgine/Foundgine/java-build.yml?branch=main&job=java-security-penetration-gate&label=Java%20Penetration%20Tests)](https://github.com/Foundgine/Foundgine/actions/workflows/java-build.yml)

[![Python Version](https://img.shields.io/pypi/v/foundgine?label=PyPI%20Python)](https://pypi.org/project/foundgine)
[![TypeScript Version](https://img.shields.io/npm/v/@foundgine/typescript?label=npm%20Typescript)](https://www.npmjs.com/package/@foundgine/typescript)
[![WASM Version](https://img.shields.io/npm/v/@foundgine/wasm?label=npm%20WASM)](https://www.npmjs.com/package/@foundgine/wasm)

# Foundgine
 
**A configurable semantic and execution boundary for application capabilities.**
 
Foundgine separates application meaning, persistence mapping, authorization and execution from the interfaces and agents that consume them. Configure capabilities with fluent APIs, resolve operations against application-defined semantics, and execute through providers—without making a particular API, ORM, database, or AI framework the center of your architecture.
 
It is built for conventional application development as well as applications that expose capabilities to AI agents. **AI is an optional consumer, not a prerequisite.**
 
## Why Foundgine?
 
Applications commonly duplicate business meaning, query logic, validation and authorization across REST endpoints, GraphQL resolvers, internal services, automation and agent tools. That duplication makes behavior harder to keep consistent and increases the cost of adding another interface.
 
Foundgine provides a configurable boundary where applications define capabilities and their semantics, map them to their persistence and domain environment, and control execution. Interfaces consume that boundary instead of independently implementing its rules.
 
- **Configure meaning, not transport:** define entities, fields, relationships, aliases and capabilities through fluent configuration.
- **Separate concerns:** semantic configuration, persistence mapping, authorization and execution are distinct responsibilities.
- **Use familiar infrastructure:** integrate with existing EF Core, JPA, databases and application architecture; Foundgine is not a mandate to replace them.
- **Reuse across consumers:** application code, supported APIs, GraphQL, MCP and agent integrations can use the same configured capabilities.
- **Adopt incrementally:** start with the operations where a shared execution boundary is useful. The semantic/AI features are optional.
## Architecture at a glance
 
```
Application / API / GraphQL / MCP / Agent / Other Consumer
                         │
                         ▼
          Configured Semantic Capabilities
                         │
          Meaning · Mapping · Relationships
                         │
                         ▼
             Resolution and Authorization
                         │
                         ▼
              Plan / Execution Boundary
                         │
                         ▼
        Provider / ORM / Existing Infrastructure
```
 
The application remains responsible for its trusted identity and security context, domain invariants, and policies. Foundgine does not infer authority from caller text, retrieval results, model output or capability discovery. Authorization is applied at the execution boundary.
 
## Security testing

Foundgine ships adversarial test suites and runnable harnesses in both .NET and Java. They check that denied requests stay denied and that protected data does not change. Counts below are test methods in the repository.

### Penetration test suites

| Suite | What it attacks | Tests | Runs in CI | Source |
|---|---|---|---|---|
| **Intent red team** | A low-privilege caller infers hidden data through filters and ordering, confuses the parser with wrong types and metadata keys, reads internals from errors, spoofs identity headers and receives another caller's results under concurrency. | 13 per language | Yes | [.NET](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Tests/IntentRedTeamPenetrationTests.cs) · [Java](https://github.com/Foundgine/Foundgine/blob/main/src/java/samples/foundgine-supply-chain-advanced/src/test/java/com/foundgine/samples/supplychain/advanced/IntentRedTeamPenetrationTest.java) |
| **Injection corpus** | One shared set of hostile payloads sent through the value, name and identity slots of an intent. | 13 per language | Yes | [.NET](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Tests/IntentInjectionCorpusTests.cs) · [Java](https://github.com/Foundgine/Foundgine/blob/main/src/java/samples/foundgine-supply-chain-advanced/src/test/java/com/foundgine/samples/supplychain/advanced/IntentInjectionCorpusTest.java) |
| **Semantic boundary suites** | Authorization escalation, tenant bypass, plan tampering, warrant replay, parser limits, secret leakage and provider conformance. | 59 .NET, 63 Java | Yes | [.NET](https://github.com/Foundgine/Foundgine/tree/main/src/csharp/tests/Foundgine.Security.Tests/Penetration) · [Java](https://github.com/Foundgine/Foundgine/tree/main/src/java/foundgine-core/src/test/java/com/foundgine/core/security/penetration) |
| **MCP and GraphQL over HTTP** | Cross-tenant reads, restricted fields, claim injection and unauthorized writes through both transports, against a seeded PostgreSQL database. | 21 (.NET) | Yes | [PenTest sample](https://foundgine.github.io/Foundgine/docs-site/samples/pentest/) · [Source](https://github.com/Foundgine/Foundgine/tree/main/src/csharp/benchmarks/AgentEndToEnd/Fixtures/SupplyChain.PenTest) |
| **Live scanners** | ZAP baseline and MCP protocol fuzzing against the running .NET sample; nmap and ZAP scripts also ship for Java. | n/a | Weekly, informational | [Workflow](https://github.com/Foundgine/Foundgine/blob/main/.github/workflows/security.yml) · [Java scripts](https://github.com/Foundgine/Foundgine/tree/main/src/java/security/pentest) |

Each suite is its own CI job. In .NET, six jobs feed `security-penetration-gate`; in Java, four feed `java-security-penetration-gate`. The gate fails if any of them fails.

### Harnesses

| Harness | What it does | How to run | Status |
|---|---|---|---|
| **Java red-team harness** ([`foundgine-redteam`](https://github.com/Foundgine/Foundgine/tree/main/src/java/security/foundgine-redteam)) | Four executable warrant checks: replay, time validity, constraint attenuation and unknown invariants. | See below | Run locally |
| **.NET AI red-team agent** ([`Foundgine.RedTeam`](https://github.com/Foundgine/Foundgine/tree/main/src/csharp/security/Foundgine.RedTeam)) | Starts PostgreSQL and the MCP APIs, then runs a bounded attack catalog against the semantic and execution endpoints. A model is optional; it refuses non-loopback targets. | `Run-Security.ps1` ([README](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/security/Foundgine.RedTeam/README.md)) | Run locally |
| **Strix and PentAGI** ([`tools/security/ai-pentest`](https://github.com/Foundgine/Foundgine/tree/main/tools/security/ai-pentest)) | Two open-source AI pentest agents pointed at the HTTP surface of both samples with identical scope. | [Run log](https://github.com/Foundgine/Foundgine/blob/main/docs/SECURITY-TESTING.md) | Harness in place, no runs recorded yet |

The [chaos and clock harness](https://foundgine.io/foundgine-overview#deterministic-testing-harness) and the [Testcontainers CI harness](https://foundgine.io/foundgine-overview#testcontainers-integration) are described in the technical overview.

### Run them

```bash
# .NET (from src/csharp)
dotnet test samples/Foundgine.SupplyChain.Advanced/Semantic/Tests/Foundgine.SupplyChain.Advanced.Tests.csproj --filter "FullyQualifiedName~IntentRedTeamPenetrationTests"
dotnet test samples/Foundgine.SupplyChain.Advanced/Semantic/Tests/Foundgine.SupplyChain.Advanced.Tests.csproj --filter "FullyQualifiedName~IntentInjectionCorpusTests"

# Java (from src/java)
mvn -pl samples/foundgine-supply-chain-advanced -am test "-Dtest=IntentRedTeamPenetrationTest" "-Dsurefire.failIfNoSpecifiedTests=false"
mvn -pl samples/foundgine-supply-chain-advanced -am test "-Dtest=IntentInjectionCorpusTest" "-Dsurefire.failIfNoSpecifiedTests=false"

# Java red-team harness (from src/java; use ; instead of : on Windows)
mvn -pl security/foundgine-redteam -am package
java -cp security/foundgine-redteam/target/classes:foundgine-core/target/classes com.foundgine.security.redteam.RedTeamHarness
```

### What this does and does not show

The red-team and injection suites run in-process against the sample's in-memory provider, so they do not exercise HTTP or SQL. The HTTP suites run against local containers with synthetic data. These are regression tests, not an independent penetration test or audit, and none has been done. See [what is covered and what is not](https://github.com/Foundgine/Foundgine/blob/main/tools/security/intent-boundary/README.md), the [security testing log](https://github.com/Foundgine/Foundgine/blob/main/docs/SECURITY-TESTING.md), and [`SECURITY.md`](https://github.com/Foundgine/Foundgine/blob/main/SECURITY.md) to report a vulnerability.

## Fluent configuration is the primary experience
 
Foundgine's configuration model is designed to be explicit and language-idiomatic in both C# and Java. The semantic model and persistence mapping are configured separately from the domain types and from transport-specific endpoints. Consult the language samples for the exact APIs supported by the current release.
 
- [.NET starter](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/samples/Foundgine.SupplyChain)
- [Java starter](https://github.com/Foundgine/Foundgine/blob/main/src/java/samples/foundgine-supply-chain)
- [Advanced .NET sample](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/samples/Foundgine.SupplyChain.Advanced/docs/00-Overview-And-Setup.md)
- [Advanced Java sample](https://github.com/Foundgine/Foundgine/blob/main/src/java/samples/foundgine-supply-chain-advanced/SupplyChain-Advanced-Tutorial.md)
## Use Foundgine without AI
 
Foundgine can be used for semantic query and execution, relationships, provider-backed operations and application interfaces without an LLM, MCP or agent framework. AI integrations are optional adapters that consume capabilities; they do not define application authority.
 
## Existing stack and incremental adoption
 
Foundgine complements rather than universally replaces:
 
- **EF Core and JPA:** ORM integration and provider paths are available; capability maturity and constraints are documented per provider.
- **GraphQL:** adapters are available for Hot Chocolate (.NET) and GraphQL Java.
- **Databases and providers:** use supported provider paths and existing infrastructure; see the provider catalogue.
- **Identity and authorization:** host/application supplies trusted execution context and policy. Foundgine is not an identity provider or authorization server.
- **Workflow and agent frameworks:** optional integrations, including Temporal and agent-framework adapters, are documented separately.
Adopt it around a selected capability, validate behavior and operational fit, then expand as appropriate. Do not assume every existing application or operation needs to move behind Foundgine.
 
## Examples and comparisons
 
- **Starter applications:** [.NET](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/samples/Foundgine.SupplyChain) · [Java](https://github.com/Foundgine/Foundgine/blob/main/src/java/samples/foundgine-supply-chain)
- **Advanced semantic and security examples:** [.NET](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/samples/Foundgine.SupplyChain.Advanced/docs/00-Overview-And-Setup.md) · [Java](https://github.com/Foundgine/Foundgine/blob/main/src/java/samples/foundgine-supply-chain-advanced/SupplyChain-Advanced-Tutorial.md)
- **Conventional stack comparisons:** [.NET — Hot Chocolate and EF Core](https://github.com/Foundgine/Foundgine/blob/main/src/csharp/samples/Foundgine.Comparison/README.md) · [Java — Spring](https://github.com/Foundgine/Foundgine/blob/main/src/java/samples/foundgine-comparison/README.md)
- [Alias grounding example](https://github.com/Foundgine/Foundgine/blob/main/docs/ALIAS-EXAMPLE.md) · [Grounding decisions](https://github.com/Foundgine/Foundgine/blob/main/docs/GROUNDING-DECISIONS.md)
Comparisons are workload-specific. Foundgine does not claim to replace every ORM, API framework or provider, nor that it is universally faster or inherently more secure than every alternative.
 
## Documentation
 
| Goal | Resource |
|---|---|
| Product overview | [What is Foundgine?](https://foundgine.github.io/Foundgine/docs-site/what-is-foundgine.html) |
| Get running | [Getting started](https://github.com/Foundgine/Foundgine/blob/main/docs/GETTING-STARTED.md) |
| Understand boundaries | [Architecture](https://github.com/Foundgine/Foundgine/blob/main/docs/ARCHITECTURE.md) · [Authorization](https://github.com/Foundgine/Foundgine/blob/main/docs/AUTHORIZATION.md) · [Security](https://github.com/Foundgine/Foundgine/blob/main/docs/SECURITY.md) |
| Browse concepts and features | [Wiki](https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/index.md) |
| Choose persistence and providers | [Provider catalogue](https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/providers.md) · [ORM providers](https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/orm-providers.md) |
| Configure extensions | [Extensions](https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/extensions.md) |
| Integrate agents and MCP | [AI agents and MCP](https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/agents-mcp.md) · [Framework integrations](https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/agent-framework-integrations.md) |
| Client SDKs | [TypeScript, Python and WebAssembly](https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/client-sdks.md) |
| Packages | [Package guide](https://github.com/Foundgine/Foundgine/blob/main/docs-site/packages) |
| Testing and evidence | [Testing](https://github.com/Foundgine/Foundgine/blob/main/docs/TESTING.md) · [Benchmarks](https://foundgine.github.io/Foundgine/docs-site/agent-benchmark/) · [PenTest](https://foundgine.github.io/Foundgine/docs-site/samples/pentest/) |
| Website | [Foundgine documentation site](https://foundgine.github.io/Foundgine/docs-site/) |
 
## Development
 
```
cd src/csharp && dotnet build Foundgine.sln && dotnet test Foundgine.sln
cd src/java && mvn -U clean verify -Dgpg.skip=true
```
 
See [testing](https://github.com/Foundgine/Foundgine/blob/main/docs/TESTING.md), [PostgreSQL E2E](https://github.com/Foundgine/Foundgine/blob/main/docs/POSTGRES-E2E.md) and [changelog](https://github.com/Foundgine/Foundgine/blob/main/CHANGELOG.md) for prerequisites and verification details.
 
## AI and discovery files
 
- [`llms.txt`](https://github.com/Foundgine/Foundgine/blob/main/llms.txt): concise discovery and canonical links
- [`llms-full.md`](https://github.com/Foundgine/Foundgine/blob/main/llms-full.md): extended technical context
- [`CONTRIBUTING.md`](https://github.com/Foundgine/Foundgine/blob/main/CONTRIBUTING.md): contributor guidance for this repository
- [`docs/AI-AGENT.md`](https://github.com/Foundgine/Foundgine/blob/main/docs/AI-AGENT.md): runtime AI integration and security boundary
- [`AGENTS.md`](https://github.com/Foundgine/Foundgine/blob/main/AGENTS.md): build, test and architecture rules for AI coding agents
- [`REPO-MAP.md`](https://github.com/Foundgine/Foundgine/blob/main/REPO-MAP.md) and [`repo-index.json`](https://github.com/Foundgine/Foundgine/blob/main/repo-index.json): directory map and complete file list
- [`docs/AI-ACCESS.md`](https://github.com/Foundgine/Foundgine/blob/main/docs/AI-ACCESS.md): clone, zip, raw files, GitMCP, DeepWiki, Context7 and crawler policy
- Hosted indexes: [GitMCP](https://gitmcp.io/Foundgine/Foundgine) · [DeepWiki](https://deepwiki.com/Foundgine/Foundgine) · [Context7](https://context7.com/foundgine/foundgine)
## License
 
Apache License 2.0.
