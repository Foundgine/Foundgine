# SupplyChain.Advanced: C# and Java parity matrix

Status is from source inspection. **Nothing in this matrix was built or run by the author**: no .NET SDK, JDK
compiler or Maven was available. Treat every "ported" row as unverified until CI is green.

Java: `src/java/samples/foundgine-supply-chain-advanced`. C#: `src/csharp/samples/Foundgine.SupplyChain.Advanced`
(plus the sibling `Foundgine.SupplyChain.Advanced.Benchmark` PostgreSQL host).

| Area | Java | C# | Status |
|---|---|---|---|
| Read intent service, caller from host, evidence | yes | yes | parity (not line-compared) |
| Semantic model, authorization policy, claims | yes (`Claims.java`) | yes (split validators) | parity (not line-compared) |
| Grounding / adversarial / graph security tests | yes | yes | parity (not line-compared) |
| Correlation id (`X-Correlation-ID`) on intent routes | yes | runtime contract + HTTP routes ported (increment 1); sample handler does not yet put it in receipts | increment 2 |
| Mutation command contract (`FoundgineMutationIntentRequest`) | yes | ported (increment 1) | done, unverified |
| Handler `HandleMutationAsync`, fail closed by default | yes | ported (increment 1) | done, unverified |
| `POST /intent/mutation` route | yes (sample endpoint) | generic route + `IFoundgineMutationDispatcher` ported (increment 1) | done, unverified |
| Typed `place_order` / `cancel_order` services (idempotency, ownership, tenant) | yes | only in the Postgres benchmark host | increment 2 |
| Approval lifecycle: dry_run, approve (opaque handle), execute_approved, replay/tenant checks, audit | yes | no | increment 2 |
| Mutation result shaped for the wire (no raw engine records) | yes | n/a until mutations exist | increment 2 |
| MCP `foundgine_mutation` through the intent boundary | yes | no (`FoundgineMcpMutationTools` takes caller-supplied approval JSON) | increment 3 |
| GraphQL `foundgineMutation` | yes | query resolver only | increment 3 |
| Mutation tests (`AdvancedMutationTest`, `HighAssuranceMutationParityTest`, endpoint lifecycle) | yes | no | increment 2 |
| Retrieval tests (fuzzy/full-text, graph similarity, search, capability) | no | yes | increment 4 (Java) |
| Benchmarks, agent, database project | no | yes | decide: port or document as C#-only |
| JPA / Postgres store, migrations | yes | EF/Npgsql in benchmark host | different stacks; compare behavior, not code |
