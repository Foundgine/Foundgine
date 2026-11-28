# Changelog

## Unreleased

### Fixed

- **Security, C# core:** `SemanticAuthorizer` did not apply read policy to filters or ordering. A caller who could not read a field (for example `Supplier.RiskScore` as a warehouse operator) could filter or order on it and infer its value or rank from the results. Authorization of a `SemanticOperation` (used by the engine and the EF reader) now refuses such queries with `SemanticAuthorizationException`, covering field, AND/OR, relationship and aggregate filters and ordering through relationship paths. The contract-less `Authorize(SemanticOperation)` overload now refuses filters and ordering that cross a relationship. The older `Authorize(SemanticGraph)` overload is unchanged and does not check query options; it is not used by the engine or the EF reader, which authorize the `SemanticOperation`. New `Authorize(SemanticContractSnapshot, SemanticGraph)` overload checks the filter and ordering in `graph.Options` (tests: `SemanticGraphQueryOptionsAuthorizationTests`). Migrate callers to it, then retire the contract-less graph overload.
- **Java core, parity with C#:** `SemanticAuthorizer.authorize(SemanticGraph)` had the same gap (it copied `graph.options()` through unchecked; the engine and JPA provider use the operation overload and were not affected). New `authorize(SemanticContractSnapshot, SemanticGraph)` overload checks the filter and ordering with the same rules as the operation path (test: `GraphQueryOptionsAuthorizationTest`, mirrors the C# `SemanticGraphQueryOptionsAuthorizationTests`). The contract-less graph overload is documented as not checking query options and should be retired once callers migrate. The earlier C# side-channel test used the unsupported `Gt` operator, so it passed without reaching authorization; it now uses `Eq` with a positive control.
- **Behaviour change, Java core:** `JsonReadIntentAdapter` now refuses wrong-typed values instead of coercing or ignoring them (`limit`, `offset`, `order`, `children`, `relationshipPath` and string properties), refuses content after the first JSON document, and no longer returns Jackson error text to the caller.

### Verification

- 28 November 2026, .NET 10: `dotnet build` and `dotnet test` on `Foundgine.sln` succeed, including the PostgreSQL end-to-end suites run against a PostgreSQL container (zero failures). Java: `mvn -U clean verify` on the Maven reactor passes (13 modules, BUILD SUCCESS, tests passing), including the Starter and Advanced samples (Advanced: 443 tests run, 0 failures, 0 errors, 1 skipped). See `docs/TESTING.md`.

### Added

- Intent-boundary red-team suites (`IntentRedTeamPenetrationTests` / `IntentRedTeamPenetrationTest`). They and the injection corpus suites are now required by the C# and Java penetration gates.

### Fixed (earlier)

- Java core: `SemanticAuthorizer` now applies read policy to fields, relationships and entities referenced by a node's filter and ordering, not only to its selections. Previously a caller who could not read a field (for example `Supplier.RiskScore` for a warehouse operator) could still filter or order on it, and the resulting row count or row order let them recover the value by bisection. Such queries are now refused with `SemanticAuthorizationException` (a 403 at the intent boundary) instead of being run or silently ignored; filters and ordering on readable fields are unchanged. The check covers field, AND/OR, relationship and aggregate filters, ordering through relationship paths, and fails closed on unrecognised expression types. Tests: `QueryOptionsAuthorizationTest` (core) and the existing `IntentInjectionCorpusTest.filteringOnAFieldTheCallerCannotReadDoesNotLeakItsValue` (supply-chain-advanced). The C# `SemanticAuthorizer` had the same gap; see the next entry.

## 3.4.2 — November 26, 2026

### Added

- C# Advanced Supply Chain: wire the typed `place_order` / `cancel_order` mutation dispatcher into the canonical API host. MCP `foundgine_mutation` and GraphQL `foundgineMutation` now share one DI-owned dispatcher and approval lifecycle; both transports enforce the same 64 KiB request ceiling. Mutation JSON rejects duplicate properties and unknown nested order-line properties. Receipts and audit entries retain correlation IDs. Customer ownership validation is consistent between preview and execution, including the tenant-B Charlie fixture. Tests cover approval single-use/context binding, replay, cross-tenant denial, both adapters, correlation IDs, duplicate/nested-property rejection and tenant-B place/cancel lifecycle. The Advanced test project references the API adapter project so those adapter tests compile as part of the suite. The approval and mutation stores remain in-memory sample fixtures, not production durability claims.
- C# Advanced Supply Chain: document the deliberate C#-only scope of the optional agent integration, benchmark projects and database project; Java ports are not required for these language-specific demonstrations. Java and C# retrieval capability parity is covered by the existing Advanced retrieval capability tests in both source trees.


- C#: `FoundgineEntityMapBuilder.Convention(...)` maps every unmapped model member to the storage member with the same name and CLR type (value types and `string`; strict, with an ignore list), and `FoundgineIdentityEntityMap<T>` in `Foundgine.Core` is the type-onto-itself shorthand. `IdentityEntityMap<T>` in the EF provider now delegates to it (it also accepts strongly typed id structs, which it previously skipped). Tests: `FluentConventionMapTests`.

- C#: the model compiler applies three defaults. (1) An entity map alone defines an entity: a model with no `FoundgineModelConfiguration` and a storage type with no `FoundginePersistenceConfiguration` take their type names, and `FoundgineModelBuilder.Semantic<T>(...)` / `Persistence<T>(...)` configure either inline. (2) With no `HasKey`, a mapped member named `Id` is the primary key. (3) A relationship with no connection gets its foreign key from member names (`N + "Id"` or `T + "Id"`, pointing at the target's `Id`, same CLR type); explicit connections win, and nothing is inferred for self-relationships or models related more than once. Error text changed: a relationship that cannot be resolved now says "no foreign key found by convention". Tests: `FluentConventionDefaultsTests`.

- Java: durable mutation execution recovery. `IMutationExecutionLedger` (in `com.foundgine.runtime.recovery`) is a write-ahead record per (tenant, subject, idempotency key) with create-if-absent and compare-and-set updates, in three implementations: `InMemoryMutationExecutionLedger`, `FileMutationExecutionLedger` (append-only JSON-lines journal, forced to disk before each call returns, a torn final line is ignored and any other unreadable line fails closed) and any host-provided transactional store. Records move through `STARTED`, `COMMITTED`, `ABORTED` and `IN_DOUBT`. `MutationRecovery` resolves executions the engine could not settle (a crash or lost response past the lease, a provider failure after dispatch, or a commit whose success audit event was never acknowledged), and an operator can resolve an `IN_DOUBT` record by hand. Success audit repair is at-least-once. `FoundgineOptions.mutationLedger(...)` and the dependency-injection wiring pass the ledger to `FoundgineMutationEngine`; without a ledger, idempotency keys are ignored. `MutationInDoubtException` reports an outcome the engine cannot know.
- Java: `IMutationReconciler` is an optional provider capability that decides from the provider's own durable state whether a dispatched mutation committed (typically a marker row written in the same transaction as the mutation). `MutationReconciliationQuery` and `MutationReconciliationVerdict` define the fencing contract. When `FoundgineOptions` sets no reconciler, the mutation provider is used if it implements the interface. The MCP server exposes `foundgine_mutation_reconcile`.
- Java: `PostgresMutationMarkerProvider` (`com.foundgine.providers.storage.sql.mutation.postgres`) is the PostgreSQL reference for `IMutationReconciler`. It owns the transaction, writes a marker row keyed by the engine's execution request id and runs a non-committing `PostgresBatchedMutationExecutionProvider` in the same transaction, so the marker is durable exactly when the mutation is. `reconcile` fences by inserting a `fenced` row under the same primary key: it waits for an open execution up to a configurable lock timeout (then answers unknown), and a late execution that finds the key taken cannot commit. A failure before commit that was rolled back is reported as `MutationNotAppliedException`; a failure at commit, or a rollback that itself fails, is never reported as not applied and is recorded as in doubt. Without a request id no marker is written and the original failure is kept. The marker table name must be a plain identifier. `ensureSchema()` creates the table. Opt-in test: `PostgresMutationMarkerProviderE2ETest` (set `FOUNDGINE_POSTGRES_CONNECTION_STRING`).
- Conformance: `src/conformance/mutation-ledger-journal.jsonl` and `mutation-ledger-scenarios.json` (generated by `tools/generate_mutation_ledger_journal.py`) fix the `foundgine-mutation-ledger-v1` journal form, and Java `MutationLedgerConformanceTest` asserts byte-for-byte record round trips, latest-version-wins reading, the unresolved list, a torn final line being ignored and a corrupt line failing closed. C# `MutationLedgerConformanceTests` (in `Foundgine.Security.Tests`) asserts the same.
- C#: `Foundgine.Runtime.Recovery` ports the mutation execution ledger: `MutationExecutionState`, `MutationExecutionRecord`, `MutationExecutionRecordJson` (the same `foundgine-mutation-ledger-v1` journal form as Java), `IMutationExecutionLedger` (async), `InMemoryMutationExecutionLedger` and `FileMutationExecutionLedger` (append-only journal flushed before each call returns, torn tail ignored, corrupt line fails closed, exclusive-open `.lock` file released by the OS when its holder dies). `MutationRecovery` (async; `ReconcileAsync`, `SweepAsync`, operator `ResolveAsync`) resolves `Started`-past-lease, `InDoubt` and unacknowledged-audit executions through the provider's `IMutationReconciler`, with at-least-once success-audit repair, three compare-and-set attempts, and a `MutationReconciliationReport`; `MutationInDoubtException` and `MutationInProgressException` are ported. `FoundgineMutationEngine` now uses the ledger (see the next entry). `MutationExecutionRecordJson` escapes strings exactly as Java's Jackson does (only `"`, `\` and control characters below U+0020; all other text, including non-BMP characters, U+007F, U+0085, U+00A0 and U+2028/2029, is raw UTF-8), so journals are byte-identical across runtimes. Tests: `MutationLedgerConformanceTests` (including a Jackson escaping check and a non-ASCII file round trip) and `MutationRecoveryTests` (every reconcile path, CAS retry and exhaustion, audit repair, manual resolution) and `FileMutationExecutionLedgerProcessTests`, the C# counterpart of Java's process test: it spawns `Foundgine.Security.Tests.LedgerChild` to run one winner per key under four racing processes, no lost compare-and-set updates, two instances in one process, a live lock holder that is never evicted, and writers killed at arbitrary points leaving a readable ledger.
- C#: `FoundgineMutationEngine` ports the Java ledger flow. New optional constructor parameters `auditLog` (default `InMemoryAuditLog`), `ledger`, `reconciler` (default: the provider when it implements `IMutationReconciler`) and `leaseDuration` (default `DefaultLease`, 5 minutes). With a ledger, an execution carrying `ExecutionContextKeys.IdempotencyKey` is recorded write-ahead before the warrant is consumed and the provider is called; a retry of a committed key returns the original result and receipt without spending the warrant or calling the provider; a key still within its lease throws `MutationInProgressException`; a failure other than `MutationNotAppliedException` throws `MutationInDoubtException` and leaves the key in doubt until `Recovery` reconciles it. `MutationExecutionResult` gains a trailing `Receipt`; `ExecutionContextKeys` gains `CorrelationId`, `IdempotencyKey` and `ExecutionRequestId` (the engine removes a caller-supplied request id and, for ledgered runs, sets its own). New exceptions `MutationCommittedUnrecordedException` and `MutationAuditPersistenceException`. The engine now also writes dry-run, approval, denial, failure and success audit events and builds an `ExecutionReceipt`, as Java does; a success audit event that cannot be persisted throws `MutationAuditPersistenceException` instead of reporting success. `IFoundgineMutations` gains a default member `Recovery` (null without a ledger). Behaviour without a ledger is otherwise unchanged, and authorization, lowering, certification and trust failures are still thrown synchronously. Built and tested on 28 November 2026: the full .NET solution builds and its tests pass (see Verification in `docs/TESTING.md`).
- C#: `PostgresMutationMarkerProvider` (`Foundgine.Providers.Storage.Sql.Mutation.Postgres`) ports the Java PostgreSQL reference for `IMutationReconciler`: marker row and mutation in one provider-owned `READ COMMITTED` transaction, fencing on `ReconcileAsync`, and `MutationNotAppliedException` only after a successful rollback. It differs from Java in four places: it re-reads the marker before commit, so work that swallowed a statement error is not reported as committed; only a unique violation from the marker insert is described as a taken request id; a lock timeout under 1 ms is rejected, because PostgreSQL reads 0 as no timeout; and a failure while disposing after commit does not replace the result.
- C# MCP: `FoundgineMcpMutationTools` gains `foundgine_mutation_reconcile`, with the same status names and JSON fields as Java. It verifies the approval token without consuming it, searches only the caller's tenant/subject scope and never executes the mutation. Unlike Java, which lists the tool only when a ledger is configured, the C# tool is always listed with the class and fails with a "not configured" error without a ledger.
- C# MCP fix: `foundgine_mutation_execute_approved` now passes the approval id to the engine as the idempotency key (`mcp-approval:<id>`, or its SHA-256 when the id is not a safe identifier), as Java does. Without it, MCP mutations were never recorded in the execution ledger and could not be reconciled. Tests: `McpMutationReconcileTests`.
- C# tests: `PostgresMutationMarkerProviderE2ETests` (in `Foundgine.E2E.Tests`, opt-in through `FOUNDGINE_POSTGRES_CONNECTION_STRING`; the table-name and timeout checks always run) covers commit, rollback, fencing, an open execution, a swallowed statement error, a unique violation inside the mutation, and an update through the default delegate. Run against a real PostgreSQL container on 28 November 2026 and passing.
- C#: `FoundgineOptions.MutationLedger`, `MutationReconciler` and `MutationLeaseDuration`, and the dependency-injection registration of `IFoundgineMutations`, pass the ledger, reconciler, lease and the registered `IAuditLog` to `FoundgineMutationEngine`. An `IMutationExecutionLedger` or `IMutationReconciler` registered in the service collection is used when the option is null. Without a ledger, idempotency keys are ignored.
- C# tests: `MutationEngineLedgerTests` (in `Foundgine.Security.Tests`) runs the real `FoundgineMutationEngine` against a fake provider: replay of a committed key without a provider call, same key with a different plan, key ignored without a ledger, malformed key thrown synchronously, abort on `MutationNotAppliedException` and retry, in-doubt refusal, in-progress lease, success-audit failure, unrecorded commit, and dry-run/approval/denial audit with the approval on the receipt.
- C#: `ExecutionReceipt` gains an optional trailing `CorrelationId` parameter (default `null`), matching Java, so ledger records round-trip the receipt's correlation id.
- C#: provider reconciliation contract in `Foundgine.Core.Execution.Mutation`: `IMutationReconciler` (async), `MutationReconciliationQuery`, `MutationReconciliationVerdict` (`Applied` requires an evidence fingerprint; `NotApplied` is binding under the fencing contract) and `MutationNotAppliedException`. `ExecutionReceipt.WithOutcome` copies a receipt with a new status, result fingerprint and completion time. `AuditCategory` gains the read and mutation categories Java already has, and `AuditEvent` gains an optional trailing `CorrelationId` and `CreateCorrelated`.
- C# fix: idempotency keys and operator ids are validated with a full-string match. The previous `$` anchor also accepted a trailing newline (`"k\n"`), which Java rejects.
- Java tests: `FileMutationExecutionLedgerProcessTest` runs the file ledger across real operating-system processes: one winner per key under four racing processes, no lost compare-and-set updates, two ledger instances in one JVM, a live lock holder that is never evicted, and writers killed at arbitrary points leaving a readable ledger. Also `FileMutationExecutionLedgerTest`, `MutationRecoveryEngineTest` and `McpReconcileRoundTripTest`.

### Changed

- C# Supply Chain samples: Advanced drops the `HasKey(x => x.Id)` lines (16 of 17), 8 of its 11 explicit connections (only the Product/ProductComponent ones remain) and 3 model configurations that only set the type name; the starter drops the `HasKey` calls, 8 of its 10 connections (`SalesOrder.Lines` and `CatalogProduct.InventoryPositions` use other key names) and the four empty `Plain<T>` configurations. Table names stay explicit because they are not the type names.
- C# Supply Chain samples: the Advanced sample's 17 hand-written identity entity maps (`SupplyChainEntityMaps.cs`) are replaced by `FoundgineIdentityEntityMap<T>` registrations in `SupplyChainModel` (same fields, same order); the starter sample drops the unreferenced `Domain/Mappings.cs` and `Domain/StorageModels.cs`.

- C#: the open-intent protocol adapters moved out of the Advanced Supply Chain sample into reusable packages. `Foundgine.Runtime` defines `IFoundgineIntentHandler` and `IFoundgineIntentPrincipalProvider`, with results carrying a transport-neutral `FoundgineIntentOutcome`; `Foundgine.Providers` adds `MapFoundgineIntentEndpoints()` and the `foundgine_intent` MCP tool; `Foundgine.Extensions.GraphQL` adds `FoundgineIntentGraphQLResolver`. The sample keeps `SupplyChainIntentService`, `IntentCaller` and `SupplyChainIntentHandler`. All three adapters require an authenticated principal; the sample's `X-Foundgine-*` header bridge runs only in Development.
- CI: the C# penetration jobs moved from `security.yml` into `csharp-build.yml` (which keeps dependency audit, SBOM and ZAP). Both `csharp-build.yml` and `java-build.yml` now use the same layout: semantic, adversarial and authorization penetration jobs, 30-minute timeouts, evidence upload on failure, a guard that fails when no tests executed, and one aggregate gate (`security-penetration-gate` / `java-security-penetration-gate`) that blocks NuGet and Maven Central publication.
- CI: the SupplyChain GraphQL/MCP penetration jobs now set `FOUNDGINE_SUPPLYCHAIN_PENTEST=1`; previously every case was skipped.
- Java: `RedTeamHarnessPenetrationTest` runs the `foundgine-redteam` harness under Maven; the Java penetration job previously never executed it.
- README: added a C# Penetration Tests badge and pointed the Java badge at the aggregate gate.

- Java: `FileMutationExecutionLedger` now takes an operating-system advisory `FileLock` on its `.lock` sidecar file instead of creating and deleting the lock file. The lock is released by the OS when its holder dies, so a crashed process no longer leaves a stale lock, a live holder is never evicted after a timeout (two processes could previously end up holding the same lock after clearing it), and the lock file is never deleted. Two ledger instances in one JVM wait for each other. Lock acquisition still times out after 30 seconds.

### Fixed

- Java: mutation plan fingerprints are now identical in every JVM. The fingerprint document is built with insertion-ordered maps; `Map.of` iterates in a different order in each JVM, which changed fingerprints across restarts and replicas and broke approvals and idempotent retries. Fingerprint values differ from earlier releases, so issue new plan approvals after upgrading.

## 3.4.1 — November 23, 2026

### Added

- Documentation: the top-level `README.md` is now a short overview; the alias-grounding example moved to a full page, `docs/ALIAS-EXAMPLE.md` (added to the MkDocs nav and docs index), and the ambiguity example links to `docs/GROUNDING-DECISIONS.md`.
- `foundgine-extensions` (Java): `GraphQLHttpHandler` (`com.foundgine.extensions.graphql.http`), the Java counterpart of the C# `GraphQLHttpEndpoint`: bearer-authenticated POST on the JDK's built-in HTTP server, parse, host `authorize` hook, fail-closed `queryRootRoles`, optional mutations, and nested result assembly from the flat row/cell result, plus `QueryProjectionTest`. The module now declares `jackson-databind` explicitly.

- `Foundgine.Extensions.GraphQL` now ships the HTTP endpoint (`Http/GraphQLHttpEndpoint.cs`, namespace `Foundgine.Extensions.GraphQL.Http`). `app.MapFoundgineGraphQL(o => ...)` maps a bearer-authenticated `POST` onto the secure query and (optionally) mutation executors, with fail-closed `QueryRootRoles`, an `Authorize` hook for claim-based rules, `AllowMutations` and `Path` options. It replaces the two copies in the Foundgine.Shop sample and the SupplyChain benchmark API, which now keep only their own rules (`ShopGraphQL.cs`, `BenchmarkGraphQL.cs`). The library project gains a `FrameworkReference` to `Microsoft.AspNetCore.App`.
- Documentation: new pages `docs/GRAPHQL-HTTP.md` and `docs/DOMAIN-PERSISTENCE-MAPPING.md` (added to the MkDocs nav), with pointers from the Mapping, AOT, Getting started and Extensions pages.
- `MappingSourceEmitter` (`Foundgine.Core/Mapping`) emits a `MappingModel` as plain C# (no reflection, JSON, `typeof` or lambdas; deterministic output), and the mapping harness gains an `emit` mode plus generated-vs-runtime structural checks and a regenerate-and-diff check (`regen-generated.sh`).

- C# Advanced Supply Chain sample: canonical intent endpoint, matching the Java sample's wire format. `POST /intent`, `POST /intent/explain` and `GET /intent/capabilities` plus the `foundgine_intent` MCP tool run a provider-neutral `ReadIntent` through a real `IFoundgine` engine per (tenant, role), with the caller taken from `X-Foundgine-Tenant` / `X-Foundgine-Role` and never from the body. New: `Semantic/Intent/*` (`SupplyChainIntentService`, `IntentCaller`, `SupplyChainRows`, `EvidencingProvider`), `Semantic/Api/Mcp/IntentEndpoint.cs`, `Semantic/Tests/CanonicalIntentEndpointTests.cs`.

- Java comparison sample (`src/java/samples/foundgine-comparison`): a baseline `spring-shop` (Spring Boot, JPA, Spring for GraphQL, Spring Security JWT) and a `foundgine-shop` (`ShopModel`, `ShopPolicy` row-level predicate, `ShopAuth` JWT and claims-to-warrant bridge, `ShopGraphQL`, `Main`, four JPA entities) over the same legacy schema, rules and parity queries as the C# comparison. `run.sh` starts both apps (:5001 Foundgine, :5002 Spring baseline) and runs the two-app `tools/parity.py` diff; `./run.sh foundgine` checks Foundgine alone. The README reports measured line counts (Foundgine 460 vs baseline 367, excluding the reusable 330-line `GraphQLHttpHandler`) and says where Foundgine costs more.

### Changed

- Enforced delegation-chain trust and revocation checks in `FoundgineMutationEngine`, matching the read execution boundary.
- Wired mutation-engine revocation and delegation-trust dependencies from `FoundgineOptions` and dependency injection.
- Revalidated warrant trust/revocation immediately before mutation dispatch and checked the revocation sequence after provider completion.
- The SupplyChain benchmark API no longer runs the temporary Development-only second mutation execution that printed exceptions (it was marked for removal and does not belong in a library endpoint). `app.MapFoundgineGraphQL()` without configuration no longer exists; callers must list their query roots.

### Fixed

- `Foundgine.Shop/Auth.cs`: `.Select(...)` was called on a collection expression (CS9176); it now uses `new[] { ... }`.

### Repository

- Removed internal working notes and assistant-tooling files, and reworded status notes in READMEs and docs so they describe the project rather than the environment it was authored in. `AGENTS.md` is now `CONTRIBUTING.md`.

### Release metadata

- Version: `3.4.1` across `Directory.Build.props`, `src/java/pom.xml` (`<revision>`), the TypeScript, Python and Rust/WebAssembly SDK packages, the MCP manifests (`mcp.json`, `.well-known/mcp.json`), the documentation and the website.

## 3.4.0 — November 18, 2026

### Added

- Added bounded FIFO provider-plan cache admission with single-flight compilation, race-safe replacement/eviction, and permanent per-engine namespace invalidation. `FoundgineEngine.InvalidateProviderPlanCache()` is a lifecycle operation: hosts must stop routing and drain in-flight requests first. Cache retirement does not touch warrant replay, revocation, or signing-key state.
- Connected the existing warrant delegation trust and revocation guards to the read execution pipeline. Delegated warrants now require an explicit root issuer, a complete root-to-leaf chain, and a configured delegation trust resolver. Warrant-backed discovery and execution fail closed when no revocation store is configured; the revocation sequence is checked again after provider execution to detect concurrent state changes.
- Java parity for batched warrant revocation lookup: `IBatchedSecurityWarrantRevocationStore` / `SecurityWarrantRevocationLookup` in core, a single-round-trip Lua `lookup` in `RedisSecurityWarrantRevocationStore`, an anchored `lookup` in `AnchoredSecurityWarrantRevocationStore` (one anchor read, no write in steady state), `SecurityWarrantRevocationGuard` using the batched snapshot when available, and `SecurityWarrantRevocationSnapshot.assertUnchanged(snapshot)`. The supply-chain benchmark filter uses the batched lookup.
- Added provider-plan cache namespace-retirement tests, including an in-flight compilation racing namespace invalidation.

### Changed

- Preserved the conservative per-warrant provider-plan cache partition. An authority-equivalence cache-key optimization was not adopted because it needs additional proof that compiled plans contain no subject-specific or request-specific values.
- `HighAssurance` supplies an in-memory revocation store only as a local-development default. Production hosts must configure a durable, shared revocation store and a delegation trust resolver when delegation is enabled.

- Redis revocation stores (C# and Java) no longer fail closed on out-of-order completion of concurrent calls: a reply is compared with the highest sequence observed *before the request was sent*, so genuine rollbacks are still detected while reordered concurrent replies are not mistaken for one.

### Changed

- Updated the wiki overview and MkDocs navigation to make the new **Why Foundgine?** page a prominent starting point for evaluators.
- Updated the website homepage with an enterprise-value section explaining the problem Foundgine solves and highlighting less duplication, more control and lower complexity. Added links to the detailed wiki and commercial strategy.
- Kept commercial claims evidence-led: the documentation describes potential benefits and recommends measuring them in a representative pilot rather than asserting unverified savings percentages or guaranteed risk reductions.
- Warrant replay stores are now bounded. `ISecurityWarrantReplayStore` gains `TryConsume(warrantId, nonce, expiresAt)` / `tryConsume(warrantId, nonce, expiresAt)` as a default interface method (existing custom stores keep working), and `SecurityWarrantReplayGuard` passes the warrant's expiry. `MemorySecurityWarrantReplayStore` previously grew forever and now evicts entries after expiry plus a one-minute skew grace. `FileSecurityWarrantReplayStore` previously re-read an ever-growing file on every consume and now compacts expired lines (new lines are `HEX<TAB>retainUntilMillis`; legacy bare-hash lines are still read and kept). The Java file store now fsyncs appends like the C# one. Shared stores (Redis, SQL) should override the new overload to set a TTL.

### Removed

- Stale nested copies of `Foundgine.Providers.Orm`, `Foundgine.Providers.Orm.Generator` and `Foundgine.Providers.EntityFrameworkCore` under `src/csharp/Foundgine.Providers/` (they were excluded from the build and had diverged from the top-level projects), five `*.migration-backup` test files, one `*.java.bak`, and a stray `src/python/.pytest_cache`. Added `src/csharp/ci/verify-no-stale-nested-projects.sh` and ignore rules for `*.migration-backup`, `*.bak`, `*.orig`, `*.rej`.

### Known limits

- The concurrent revocation sequence check detects a change during provider execution but cannot roll back side effects a provider has already committed; strict atomic revocation-versus-side-effect guarantees require provider transaction/authorization coordination.
- The revocation-store interface has no freshness/health contract, so distributed-store staleness must be handled by the concrete store implementation before production use.

### Performance evidence

- Recorded a preliminary C# SupplyChain closed-loop run in `docs/performance/SUPPLYCHAIN-CSHARP-PRELIMINARY-RESULTS.md`. On the `sc-cs-orders-customer` read scenario, the plain Hot Chocolate + EF Core baseline had about 2.3x to 3.1x the throughput and lower p99 than the Foundgine arm at concurrency 1, 8 and 16 (the cells where both were valid); both arms were invalid from concurrency 64. This is preliminary and not claim-grade: one scenario, no recorded equivalence gate or resource telemetry, no Java results. The page lists likely causes as hypotheses with checks. The website performance page links to it.

- Added `tools/foundgine-perf/java/` (generate-baseline, generate-foundgine and verify-migration-provenance scripts), mirroring `tools/foundgine-perf/csharp/`, with `tests/test_java_migration_scripts.py`.

### Fixed

- Java SupplyChain benchmark arms did not start under Spring Boot 4: both applications now depend on `spring-boot-starter-flyway` (Flyway auto-configuration is no longer part of `flyway-core`, so no migration ran and Hibernate validation reported a missing table), and the Foundgine arm marks the anchored revocation store `@Primary` so the readiness controller and filter no longer see two `ISecurityWarrantRevocationStore` beans. Also: both Java arms override `graphql-java` to 25.0 (the parent pins 22.3, which breaks Spring GraphQL 2.0 with `NoClassDefFoundError: org.dataloader.DataLoaderOptions$Builder`); the Foundgine arm creates the revocation anchor table only after its Flyway migration (`@DependsOn`), and the compose profile sets `SUPPLYCHAIN_SEED_DEMO=false` so the harness alone seeds both arms. Both Java arms also compile with `-parameters` (the parent is not `spring-boot-starter-parent`, so Spring GraphQL `@Argument` names were missing), and the Foundgine arm now includes `spring-boot-starter-web` (without it the application was not a web app and Spring Security had no `HttpSecurity`). Not yet rebuilt or run; verify with `docker compose ... up -d --build` and the schemaparity/armauthz gates.

- Split the SupplyChain benchmark Postgres init script into `tools/foundgine-perf/compose/csharp/supplychain-init.sql` and `compose/java/supplychain-init.sql` (each creates only its runtime's databases); `compose/supplychain.yml` mounts both. This only changes first-time database creation: on an existing Postgres volume the init scripts do not re-run.

- Fixed the `Publish NuGet packages` step in `.github/workflows/csharp-build.yml`: it still required exactly four packages after the package restructuring, so publishing failed with "Expected exactly four packages to publish" even though validation accepted the ten expected packages. It now requires ten, consistent with the validation steps.

- **Four NuGet packages, aligned with the four Maven artifacts.** .NET now publishes exactly `Foundgine.Core`, `Foundgine.Runtime`, `Foundgine.Providers` and `Foundgine.Extensions` (Java: `foundgine-core`, `-runtime`, `-providers`, `-extensions`). `Foundgine.Providers` bundles the ORM, EF Core, Redis, Microsoft Agent Framework, pgvector and Elasticsearch assemblies plus both generators; `Foundgine.Extensions` bundles the GraphQL, Temporal and Keycloak assemblies and depends on `Foundgine.Providers`. They are produced by two packaging-only projects (`Foundgine.Providers.Package`, `Foundgine.Extensions.Package`) because the integration projects reference `Foundgine.Providers` and cannot be compiled into it; the integration projects are now `IsPackable=false`. C# source layout, namespaces and project references are unchanged; install snippets for the former separate packages now point to `Foundgine.Providers` or `Foundgine.Extensions`. Updated the `csharp-build.yml` package validation and publish steps (four packages, new content checks) and `ci/pack-providers.sh` / `ci/verify-providers-package.sh`. Run `ci/pack-providers.sh 3.4.0` and the CI validation before tagging.

### Release metadata

- Updated the .NET (`Directory.Build.props`) and Java (`src/java/pom.xml` `<revision>`, generated flattened POMs) canonical versions, the TypeScript, Python and Rust/WebAssembly SDK package metadata and lockfiles, client user-agent/`clientInfo` versions, MCP manifests (`mcp.json`, `.well-known/mcp.json`), README release notes and install snippets, module READMEs, the packages page, website metadata and footers, `llms*.txt` and the documentation coverage pages to `3.4.0`.
- The Microsoft Agent Framework adapter, Java PostgreSQL migrations and Redis/shared-state work previously listed after the `v3.3.5` tag are now part of the `3.4.0` source. Because this is a minor release, note the package restructuring under **Changed**: optional adapters and extensions are separate NuGet packages.

## 3.3.5 — November 15, 2026

### Added

- Added the Python SDK as a source-layout package with a Python 3.11–3.14 test matrix, lint/type-check jobs, package build checks and a PyPI trusted-publishing workflow.
- Added dedicated TypeScript and Rust/WebAssembly SDK workflows for tests, package creation and release publishing. The TypeScript workflow tests Node.js 20 and 22; the WASM workflow runs Rust, wasm-bindgen and JavaScript checks.
- Added shared conformance-vector checks to the SDK CI workflows.

### Changed

- Moved the Python, TypeScript and Rust/WebAssembly SDK package contents to the top level of their respective SDK directories and updated package metadata, lockfiles, build paths and install documentation.
- Updated the .NET and Java canonical versions and SDK package metadata to `3.3.5`.
- Replaced the previous combined CI workflow with dedicated C#, Java, Python, TypeScript and WebAssembly workflows, and updated the npm publishing workflow.
- Updated MCP manifests, README installation instructions, package pages and generated documentation-site metadata for the `3.3.5` release.

### Fixed

- Hardened Java MCP approval-token verification against RSA signature tampering and updated the interoperability test.

## 3.3.4 — November 14, 2026

### Fixed

- Corrected Python SDK Ruff diagnostics: `TokenProvider` union ordering, module/class spacing, and constructor formatting.
- Corrected the C# CI test-report assertion to aggregate executed tests across all TRX reports rather than searching for absent test class names.
- Corrected the Java CI assertion to inspect the actual `FoundgineTemporalWorkflowTest` Surefire report and require a non-zero test count.

### Changed

- Aligned TypeScript, Python, Rust/WASM and their package metadata/client user-agent versions with the Foundgine `3.3.4` release line.
- Added Rust/WASM `listCapabilities()` and `execute()` aliases and top-level notification support while preserving the existing `listTools()` and `callTool()` methods.
- Aligned TypeScript and Rust/WASM URL joining and RFC 3986 path-segment encoding; added Rust/WASM notification replay and reconnect options.
- Added `SDK-PARITY.md` to define platform-runtime versus transport-client contracts and record remaining parity gaps without overstating support.
- Updated current documentation, MCP manifests, package examples and generated Maven flattened POM metadata to `3.3.4`.
- Re-enabled the Java Maven Central publish job for `v*` release tags, gated on the Java build, security penetration and PostgreSQL E2E jobs.
- Made npm and PyPI publication depend on the Rust/WASM SDK test job as well as the applicable SDK and server test jobs.

## 3.3.3 — November 12, 2026

### Fixed

- Corrected the `Foundgine.Providers` NuGet package composition: the package now includes the Foundgine-owned ORM and EF Core provider assemblies, plus the AOT and ORM generators as analyzers.
- Declared EF Core 10.0.0, EF Core Relational 10.0.0 and Microsoft.Extensions.DependencyInjection.Abstractions 10.0.0 as package dependencies instead of bundling third-party DLLs.
- Added CI package checks for required Foundgine assemblies and analyzers, NuGet dependency declarations, and exclusion of third-party EF Core / DI DLLs.

### Changed

- Aligned canonical .NET and Java versions, MCP manifests, install snippets, website metadata and AI reference files on `3.3.3`.
- Updated current package descriptions and READMEs to describe Foundgine 3.x rather than the obsolete product-level “Foundgine v2” label. Historical protocol/schema version identifiers and benchmark evidence labels remain unchanged.
- Preserved the internal ORM, EF Core and generator project boundaries while packaging their Foundgine-owned outputs through `Foundgine.Providers`.

## 3.3.2 — November 12, 2026

### Added

- Added the Java JPA persistence path: an ORM-neutral execution layer, JPA reader and repository APIs, entity-model registration, and Spring Boot JPA auto-configuration.
- Added Java AOT/JPA metadata support, including generated direct-access entity models and a native-image JPA sample with reachability metadata.
- Expanded the Java starter and Advanced Supply Chain samples with JPA-backed persistence examples and integration tests.
- Added the Java ORM/JPA parity plan and package-content checks for the published provider artifacts.

## 3.3.1 — November 11, 2026

### Changed

- Aligned the canonical Foundgine .NET and Java release versions on `3.3.1`
  (`VersionPrefix` in `Directory.Build.props` and `<revision>` in
  `src/java/pom.xml`).

### CI

- `build.yml` and `java-build.yml` no longer hardcode the canonical version.
  `build.yml` reads `VersionPrefix` from `Directory.Build.props` and
  `java-build.yml` reads `<revision>` from `src/java/pom.xml`. A release tag
  must equal that value, and non-tag builds are versioned
  `<canonical>-ci.<run-number>`. This fixes release tags such as `v3.3.1`
  failing with `Release tag … does not match canonical version 3.3.0`, and
  means the next release only needs the two version files bumped.

### Java build

- The root `pom.xml` now activates `maven-source-plugin` and
  `maven-javadoc-plugin` (with `doclint` disabled), so every published module
  produces the `-sources` and `-javadoc` jars Maven Central requires. They were
  previously declared in `pluginManagement` only and never executed.
- `foundgine-aot-generator` is excluded from the Central Portal deployment,
  matching its documented role as a reactor-only annotation processor.

### Providers

- **Java JPA provider (`storage.orm.jpa`).** Hibernate ORM 7.4.15.Final with H2
  2.5.252 and a PostgreSQL persistence unit are now test-scoped dependencies,
  with SQL statement capture, Criteria tuple-query integration tests,
  `EntityManager` ownership checks and owned/caller-owned transaction coverage.
  New or extended capabilities, all implemented-unverified until the Java 27
  H2/PostgreSQL workflow passes:
  - Scalar translation: typed, per-execution named `ParameterExpression`
    binding for comparisons and every `IN` member; explicit empty and NULL-only
    `IN` handling; mapped-type checks; an ordering allowlist (boxed numerics,
    `BigDecimal`/`BigInteger`, `String`, supported `java.time` types) that
    rejects enum and UUID ordering.
  - Relationship predicates: correlated `EXISTS` and `SOME`/`NONE`/`ALL`
    quantifiers over the authorization-visible child set (ALL is vacuously true
    when no authorized child exists), bounded by `OrmPredicateValidator`
    (256 nodes, nesting depth 8, cycle detection).
  - Authorized relationship aggregates (`COUNT`, `MIN`, `MAX`) as filters, and
    non-paged one-hop aggregate ordering.
  - Aggregate cursor groundwork and execution integration: canonical typed
    cursor values, plan-bound cursor envelope with a SHA-256 fingerprint,
    private aggregate tuple transport, and `JpaAggregateContinuation`
    lexicographic continuation predicates.
  - Opt-in `JpaOrmSnapshotPageSession`: one `EntityManager` and transaction for
    a whole multi-page read. The caller must acknowledge
    `REPEATABLE_READ_OR_STRONGER`, because JPA cannot set or inspect
    isolation portably. H2 and PostgreSQL concurrency tests are defined.
- **.NET EF Core provider.** `Foundgine.Providers.Orm` and
  `Foundgine.Providers.EntityFrameworkCore` carry the explicit, versioned
  `OrmProviderCapabilities` declaration (relationship filters, aggregate
  filters, aggregate ordering, per-parent pagination as separate features), and
  `FluentEfModel.Apply` maps fluent Foundgine metadata onto an EF Core model
  (table, key and column names from the metadata; navigation members ignored).
  Aggregate cursor pagination stays disabled until translation and runtime
  tests pass.
- **Conformance evidence.** The .NET matrix
  (`src/csharp/conformance/orm/provider-conformance-matrix.json`) and the Java
  plan (`src/java/conformance/orm-jpa-parity-plan.json`) record translation, runtime and
  Native AOT status per capability. Source presence or a configured CI job is
  not counted as passing evidence.

### Samples

- **.NET Supply Chain starter** (`src/csharp/samples/Foundgine.SupplyChain`)
  runs on EF Core with `Npgsql.EntityFrameworkCore.PostgreSQL`, referencing
  `Foundgine.Providers.Orm` and `Foundgine.Providers.EntityFrameworkCore`.
- **Java Supply Chain starter**
  (`src/java/samples/foundgine-supply-chain`) now has a JPA + PostgreSQL read
  path (`SupplyChainJpa`, `SupplyChainSemantics`, `SupplyChainPolicy`) mapping
  `Supplier`, `Warehouse` and `Product`; `SupplyChainSemanticsTest` exercises
  the semantic, authorization and planning half against the in-memory ORM
  source. The MCP transport and mutation path are not wired in this starter.
- Java advanced sample gains `AdvancedJpaSemanticsTest`.
- Starter tutorials and README files updated for the new provider wiring and
  version.
  
### Documentation

- Corrected the ORM provider status table in `docs/wiki/orm-providers.md`:
  Java relationship filters (`SOME`/`NONE`/`ALL`) and aggregate filters were
  listed as unsupported or unqualified, but the JPA predicate translation
  exists and is implemented-unverified. Semantic filter lowering to those
  predicates is not wired, so the shapes still fail closed end to end. The
  .NET cells now state that translation and runtime evidence is
  test-defined, not run, and Java aggregate ordering now lists count, min and
  max.
- Fixed a stale bullet in the Java ORM README
  (`storage/orm/README.md`) that listed relationship quantifiers, aggregate
  filters and aggregate ordering as not implemented, contradicting its own
  step sections.

### Release

- Version: `3.3.1`
- Target framework: `.NET 10` / Java 27
- License: Apache License, Version 2.0

## 3.3.0 — November 11, 2026

### Changed

- Aligned the canonical Foundgine .NET and Java release versions on `3.3.0`.
- Updated NuGet package metadata, Maven reactor and flattened consumer POMs,
  CI package versioning, MCP manifests, installation examples, public
  documentation, website metadata and AI reference files to `3.3.0`.
- Preserved third-party dependency versions; Foundgine release numbering is
  independent of the versions of .NET, Java, xUnit, Model Context Protocol,
  Maven plugins and other external dependencies.
- Fixed Docker metrics summarization under PowerShell strict mode by
  normalizing collector errors to an array and restoring the zero-sample
  error-handling branch.

### Release

- Version: `3.3.0`
- Target framework: `.NET 10` / Java 27
- License: Apache License, Version 2.0

## 2.3.1 — November 9, 2026

### Changed

- Aligned the canonical .NET and Java release versions on `2.3.1`.
- Updated the checked-in Maven consumer POMs, MCP discovery manifests, package
  installation examples, public documentation, website metadata and AI
  reference files to identify `2.3.1` as the current release.
- Aligned the .NET CI build's non-tag package version with the `2.3.1` release
  line (`2.3.1-ci.<run-number>`).
- Removed stale per-package `2.0.0` release-note overrides so all NuGet
  packages inherit the shared changelog metadata.
- Corrected three broken project-reference paths in the Run1 benchmark project.
- Removed a tracked, machine-generated TRX test report that contained stale local
  .NET 9 build paths; retained the human-readable and JSON benchmark reports.

### Release

- Version: `2.3.1`
- Target framework: `.NET 10` / Java 21
- License: Apache License, Version 2.0

### Git tag follow-up — `v3.2.2` (November 10, 2026)

The `v3.2.2` Git tag points to source whose .NET and Maven package versions are still `2.3.1`; it is not a separate `3.2.2` package release. That tagged source moved the Java build to Java 27 and added:

- the Java AOT annotation processor in `foundgine-providers`, its service registration and a processor parity test;
- the Java build guide and Java 27 CI toolchain updates;
- the corresponding Supply Chain sample and annotation-processor wiring updates.

The package version remained `2.3.1`, so these changes are recorded here as a tag follow-up rather than an invented `3.2.2` release section.

## 2.3.0 — November 8, 2026

### Added

- **Fluent configuration API for .NET and Java.** A second, code-first way to
  produce the same `MetadataRegistry` that the `[Foundgine*]` attributes /
  annotations and the AOT generator produce. Each configuration class owns one
  concern — semantic entity and relationships, persistence (table, key, columns,
  indexes), model-member-to-storage-member maps, relationship-to-foreign-key
  connections, and model-to-entity connections — and a single `FoundgineModel`
  subclass registers them. `Build()` validates the whole set and reports every
  problem in one exception; `BuildInto(registry)` adds the result to an existing
  registry and refuses to overwrite ids that are already registered.
  - .NET: `Foundgine.Core.Semantic.Configuration` (`FoundgineModel`,
    `FoundgineModelConfiguration<T>`, `FoundginePersistenceConfiguration<T>`,
    `FoundgineEntityMap<TModel, TEntity>`, `FoundgineConnectionConfiguration`,
    `FoundgineModelConnectionConfiguration<T>`).
  - Java: `com.foundgine.core.semantic.configuration` with the same shape
    (`FoundgineModel.build()` / `buildInto(...)`).
- **Model connections with conversions and authorization.** `HasConnection` /
  `hasConnection` maps a model member to a storage entity; `Maps(source, target,
  convert)` records a `ConversionMetadata` for the type pair, and `Authorize` /
  `authorize` reads the predicate as an expression (never executed at run
  time) into an `AuthorizationPredicate`.
- `ColumnMetadata.MaxLength` and `ColumnMetadata.IsRequired` (.NET), populated by
  `HasMaxLength` / `IsRequired`.
- Samples now configured fluently: the .NET Supply Chain starter
  (`src/csharp/samples/Foundgine.SupplyChain`, `Domain/SupplyChainModel.cs`) and
  the .NET and Java Supply Chain advanced samples. Application code uses the
  `SupplyChainSemantics` handles, which are checked against the compiled registry
  at first use.

### Changed

- **Target framework is now .NET 10.** Every C# project under `src/csharp`
  already built for `net10.0`, while the repo defaults, CI and docs still said
  .NET 9. `Directory.Build.props` now defaults to `net10.0`, the `build.yml` and
  `security.yml` workflows install the `10.0.x` SDK, and every doc, website page,
  `llms*` file and `mcp.json` reads ".NET 10". Java stays on 21.

### Documentation

- Aligned the whole repository and website on `2.3.0`: `Directory.Build.props`,
  `README.md`, `llms.txt`, `llms-full.md`, `docs/`, `docs-site/`
  (install snippets, hero badge and every page footer) and the Java reactor
  (`<revision>` and the flattened POMs).
- `mcp.json` and `.well-known/mcp.json` had drifted to `2.0.3` and now read
  `2.3.0`; `.well-known/mcp.json` also pointed `mcpProject` at a path that does
  not exist (`src/Foundgine.Providers/...`) and now matches `mcp.json`.
- Documented the fluent API in `docs/MAPPING.md`, `docs/PUBLIC-API.md`,
  `docs/ARCHITECTURE.md`, `docs/GETTING-STARTED.md`, `docs/CURRENT-STATUS.md`
  and the website's Get started page.
- Rewrote the sample tutorials for the fluent API: the .NET starter
  tutorial and "explained" guide, and the Java starter tutorial, advanced
  tutorial and "explained" guide. They previously walked through attribute
  declarations and generated `GeneratedSemanticModel` / `GeneratedMetadata`
  types that the samples no longer use.

### Known limits

- Composite primary keys and composite foreign keys are not supported by the
  fluent API (or by Foundgine metadata); `Build()` reports them.
- Type conversions are available on model connections only; entity maps still
  require identical CLR/Java types.
- Convention-based connection fields are not inferred; list each `Maps(...)`.
- Discovery of `FoundgineModel` subclasses by the source generator is not
  implemented; `Build()` runs at startup.

### Release

- Version: `2.3.0`
- Target framework: `.NET 10` / Java 21
- License: Apache License, Version 2.0

## 2.2.1 — September 14, 2026

### Documentation

- Fixed version drift across public docs and the website: several pages
  (`docs/CURRENT-STATUS.md`, `docs/README.md`, `docs/PUBLIC-API.md`,
  `llms.txt`, `llms-full.md`, `docs-site/llms.txt`, `docs-site/llms-full.md`,
  and 21 `docs-site/**/index.html` page footers) still described the .NET line
  as `2.0.1` and/or the Java line as `2.2.0`, even though 2.2.0 already
  aligned the two ecosystems onto a single shared version number. The
  `docs-site` install snippet (`dotnet add package Foundgine.Runtime/Providers
--version 2.0.1`) and the homepage hero badge (`v2.0.1`) had the same
  problem. All now read `2.2.1` consistently.

### CI

- Fixed `.github/workflows/java-build.yml`'s `java-security-penetration` job,
  which called `mvn -pl security/pentest -am verify` — `security/pentest`
  has never been a Maven module (it's the live-scanner script directory, no
  `pom.xml`), so the job failed outright. It now runs
  `security/pentest/run-all.sh` with `SKIP_LIVE=true`.
- Fixed the same underlying bug inside `run-all.sh` itself, plus a second,
  previously-latent one: `ROOT` only climbed one directory from
  `security/pentest`, landing in `security/` (no `pom.xml`) instead of
  `src/java`, which also broke the script's live-check paths
  (`run-nmap.sh`/`run-zap.sh`) whenever `SKIP_LIVE` was unset.
- `publish-nuget` (`build.yml`) previously did not wait on
  `supplychain-sample-tests`, `supplychain-semantic-sample-tests`, or any
  `security.yml` job before publishing to NuGet — the latter was structurally
  impossible, since `needs:` cannot reference a job in a different top-level
  workflow. `security.yml` now also triggers via `workflow_call` (its
  `schedule`/`workflow_dispatch` triggers are unchanged), `build.yml` calls it
  through a new `security-gate` job, and `publish-nuget` now depends on all of
  the above plus `security-gate`.
- `publish-maven` (`java-build.yml`) previously did not wait on
  `java-security-penetration` before publishing to Maven Central. It now does.

### Release

- Version: `2.2.1`
- Target framework: `.NET 9` / Java 21
- License: Apache License, Version 2.0

## 2.2.0 — September 14, 2026

### Security

- Resolved the open question in `docs/SECURITY.md` ("the semantic API's
  response verbosity"): `SupplyChainMcpTools.PolicyProbe` (the sample
  `policy_probe` MCP tool under `src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Api/Mcp`)
  now only returns its full `kind`/predicate/named-claim decision detail
  when the host is running in a development environment
  (`IHostEnvironment.IsDevelopment()`). In every other environment it logs
  the full decision server-side via `ILogger`, keyed by a short opaque
  correlation id, and returns only `{ allowed: false, reference: <id> }` —
  matching the correlation-id pattern `Semantic/Api/Mcp/BenchmarkHost/Program.cs`'s `Execute`
  helper already used for the execution API, so a caller probing many
  `attack` variants can no longer use the response shape to map policy
  boundaries.

### Release

- The C# NuGet packages and Java Maven artifacts now ship as aligned,
  same-numbered releases: `Foundgine.Core`/`foundgine-core`,
  `Foundgine.Runtime`/`foundgine-runtime`,
  `Foundgine.Providers`/`foundgine-providers`, and
  `Foundgine.Extensions`/`foundgine-extensions`.
- Corrected the NuGet package metadata's license expression from `MIT` to
  `Apache-2.0`. The packaged `LICENSE` file was always the Apache License,
  2.0 text; only the `PackageLicenseExpression` in `Directory.Build.props`
  disagreed with it. Added an equivalent `<licenses>` declaration to the
  Java `src/java/pom.xml` parent so both ecosystems advertise the same
  license.
- The Java build previously hardcoded `2.0.0-SNAPSHOT` as a literal string
  in ten separate `pom.xml` files (the parent's own `<version>`, every
  module's `<parent>` reference, and most intra-repo `<dependency>`
  versions), even though a `<revision>` property already existed and was
  already used correctly in a few places (e.g. the sample projects'
  annotation-processor path). That property was otherwise dead — nothing
  read it. `src/java/pom.xml` now sets `<version>${revision}</version>` on
  the parent itself, every module and intra-repo dependency below it
  references `${revision}`, and `flatten-maven-plugin` resolves that
  property into a concrete version in any installed/deployed POM. This
  makes `revision` the single source of truth for the Java release version,
  the equivalent of `VersionPrefix` in `../../Directory.Build.props` for
  the C# tree.
- Added missing `<description>` elements to the `foundgine-runtime` and
  `foundgine-providers` POMs (mirroring `Foundgine.Runtime` and
  `Foundgine.Providers` on the C# side); `foundgine-core` and
  `foundgine-extensions` already had one.
- Version: `2.2.0`
- Target framework: `.NET 9` / Java 21
- License: Apache License, Version 2.0

## 2.1.0 — September 8, 2026

### Samples

- Added a generic, authorization-focused MCP server (`Foundgine.SupplyChain.Advanced.Mcp.Api` under `src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Api/Mcp`) exposing `describe_capabilities`, `read_entity`, `read_relationship`, `write_entity`, and `policy_probe` tools directly against the sample's composed semantic model. This is additive: it runs standalone and is not wired into the Supply Chain E2E harness.

### Fixed

- The commit that added the generic MCP server also deleted `src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Api/Mcp/BenchmarkHost` (the Postgres-backed MCP server the Supply Chain E2E `Agent` and `run-supply-chain.ps1` actually depend on — `get_order`, `get_inventory`, `list_suppliers`, and the rest of the Agent's tool contract) along with its `docker-compose.yml` service and solution entries, without updating the harness. This broke `run-supply-chain.ps1` at step 3/8 with `no such service: mcp-foundgine`. Restored the `MCP.Foundgine` project, its `docker-compose.yml` service, and its `Foundgine.sln` entries so the Supply Chain E2E + PenTest harness runs again.
- Reverted an unrelated, uncommitted in-progress edit to `src/csharp/benchmarks/AgentEndToEnd/Run5/docker-compose.yml` that had stripped its `mcp-foundgine` service block while the project files were left in place, which would have broken Run5 the same way.

### Release

- Version: `2.1.0`
- Target framework: `.NET 9`
- License: MIT

## 2.0.3 — September 7, 2026

### Semantic grounding

- Added `CandidateTruncation` diagnostics to `SemanticLexicalResolver`: when a token's retrieved candidate set exceeds `candidateLimit`, the resolver now records how many candidates were kept vs. discarded, the retrieval score at each side of the cut, and the resulting `MarginGap`. Candidates cut at retrieval time never reach graph search, so previously they were simply gone with no record of whether one might have represented a distinct, legal meaning.
- `GroundingDecision` gains `TruncationRisks` (empty, never null, via `EffectiveTruncationRisks`). When the leading interpretation depends on a token whose highest-scoring truncated candidate falls within the resolver's ambiguity margin of the lowest candidate it kept, the outcome is now `RequiresClarification` instead of `Committed` — the same "don't silently pick a winner inside the margin" policy that already governs surviving interpretations, applied one stage earlier to a candidate that never got the chance to survive or fail on its merits.
- This is diagnostic and fail-closed only: it can turn a `Committed` outcome into `RequiresClarification`, but never the reverse, and it does not change grounding for expressions with no truncation.

### Tests

- Added `SemanticLexicalResolverTests` coverage for the new truncation-risk path: within-margin truncation forcing clarification, clear-margin truncation still committing, and the diagnostic being empty when no truncation occurs.

### Release

- Version: `2.0.3`
- Target framework: `.NET 9`
- License: MIT

## 2.0.2 — September 6, 2026

### Semantic grounding

- Fixed `SemanticLexicalResolver.Ground` so the compact-name fallback (e.g. `customer profile` → `customerprofile`, `purchase order` → `purchaseorder`) is actually reached: the initial per-token retrieval pass now stops as soon as any single token comes back with no candidates, instead of continuing to query the remaining tokens first. Continuing to query every token before falling back was spending the shared retrieval-timeout budget on results that were about to be discarded anyway, which could starve the compact-fallback lookup of the time it needed and made it appear as though the fallback simply hadn't run.
- Fixed a latent `KeyNotFoundException` in the "no lexical candidate" diagnostic: if the compact-name fallback itself also returned no candidates, the resolver could try to look up a token that had never been queried (because retrieval stopped early per the fix above) and crash instead of reporting `GroundingOutcome.Unresolved` cleanly.

### Tests

- Widened the timing margins in `Ground_uses_one_retrieval_deadline_across_compact_token_fallback` (both `Foundgine.Semantics.Tests` and the `Foundgine.SupplyChain.Advanced` sample) so the assertions no longer depend on sub-15ms real-time scheduling precision. Windows' default thread-timer resolution can silently round a `Thread.Sleep(1)` up to ~15ms, which was enough on its own to exhaust the previous 10ms retrieval budget before the compact-token lookup even started. The test now uses a `TimeSpan.Zero` sleep for the always-fast lookup and a 200ms budget with a 500ms fallback delay, comfortably clear of any plausible scheduler jitter, while asserting exactly the same behavior.

### Release

- Version: `2.0.2`
- Target framework: `.NET 9`
- License: MIT

## 2.0.1 — September 5, 2026

### Semantic grounding

- Fixed retrieval timeout accounting so one retrieval deadline is shared across all tokens and the compact-name fallback; provider overruns are detected after synchronous retrieval returns, and cancellable providers receive a linked timeout token.
- Corrected `candidateLimit` documentation to describe the implemented total per-token limit across semantic kinds.
- Renamed `GroundingInterpretation.Confidence` to `InterpretationScore` to make its heuristic ranking semantics explicit.
- Clarified that ambiguity is based on score separation within the configured ambiguity margin rather than a literal tie.
- Clarified that alias `Weight` is application-declared evidence weight, not confidence or priority.
- Documented that `CompetingInterpretations` is internal semantic metadata and should be projected safely before exposure to untrusted callers.
- Corrected the advanced Supply Chain weighted-alias test so it asserts only the semantic identity that actually participated in lexical grounding.

- Restored deterministic alias/synonym grounding for generated semantic contracts.
- Canonical semantic identity is independent of caller wording, so a declared alias resolves to the same interpretation as its canonical name.
- Retrieval representations of the same stable semantic identity no longer create false ambiguity during grounding.
- Added a bounded compact-name fallback for multi-word canonical names such as `purchase order` when token-by-token lexical retrieval does not produce a candidate.
- Advanced Supply Chain grounding coverage explicitly exercises the README case:
  - `purchase orders` / `buys` → `PurchaseOrder`
  - `supplier` / `seller` → `Supplier`
- Added optional alias evidence weights (1–100) for entity, field, and relationship aliases, with `AliasWeightEvidenceGate` for thresholded, fail-closed evidence evaluation that never grants authorization.

### Documentation

- Updated the root README to explain the canonical request and its alias-matched paraphrase layer by layer.
- Added a concrete PlantUML source and matching SVG for the alias-matched Supply Chain execution path under `docs/assets/`.
- Documented that alias matching changes vocabulary, not authority: retrieval remains discovery, while authorization remains application-controlled.

### Release

- Version: `2.0.1`
- Target framework: `.NET 9`
- License: MIT

- Java parity: added Runtime plan-approval E2E tests covering successful approved execution, semantic-version tampering, and plan-fingerprint tampering.
