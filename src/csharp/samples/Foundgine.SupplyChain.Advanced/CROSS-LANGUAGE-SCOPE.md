# Cross-language scope

The Advanced sample deliberately distinguishes shared semantic capabilities from language-specific demonstrations.

## C#-only demonstrations

The following remain C#-only in this release; Java equivalents are not implied and are not required for parity:

- **Agent integration** — the .NET agent host and its framework-specific integration are an optional consumer of the semantic boundary.
- **Benchmark projects** — the C# benchmark harnesses and their measured reports are independent evidence for those .NET implementations. They are not Java performance claims.
- **Database project** — the standalone C# database/EF Core demonstration is a persistence integration example, not a required second implementation of the Java JPA sample.

These are intentionally documented as C#-only rather than ported as superficial placeholders. Shared semantic contracts, authorization invariants, mutation lifecycle behavior, and retrieval capability declarations should still be tested in both languages where the implementation supports them.

## Mutation boundary

The C# semantic API exposes `foundgine_mutation` over MCP and `foundgineMutation` over GraphQL. Both adapters call the same application-owned `SupplyChainMutationDispatcher`; neither adapter implements its own authorization or execution path. The dispatcher resolves the authenticated host principal, enforces tenant and role boundaries, validates the command shape, and delegates to typed `PlaceOrderService` / `CancelOrderService` operations.

Supported lifecycle modes are `dry_run`, `approve`, `execute`, and `execute_approved`. Approval handles are opaque, short-lived, single-use, and bound to subject, tenant, role, tool, and the planned request fingerprint. Receipts and audit entries carry correlation IDs. The sample's mutation store and approval store are in-memory teaching fixtures; production applications must provide durable transactional persistence, durable replay protection, and an audit sink appropriate to their deployment.

## Verification

- C# mutation lifecycle and adapter tests: `Semantic/Tests/MutationLifecycleTests.cs` and `Semantic/Tests/MutationTransportAdapterTests.cs`.
- C# retrieval capability and provider tests: `Semantic/Tests/Retrieval/`.
- Java retrieval capability parity: `src/java/samples/foundgine-supply-chain-advanced/src/test/java/com/foundgine/samples/supplychain/advanced/SupplyChainRetrievalCapabilityParityTest.java` and the related provider retrieval tests.

Run the .NET 10 and Java 27 test commands from the repository's `AGENTS.md`. A source-level test or a test in the other language is not a substitute for executing the relevant build.
