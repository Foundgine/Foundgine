# Foundgine Runtime (Java)

The runtime is the application-facing execution layer above `foundgine-core`. It coordinates
semantic intent compilation, resolution, authorization, planning, approval, provider dispatch,
evidence, mutation execution and task routing. It does not provide a database provider, HTTP server,
GraphQL server, MCP transport, identity provider or LLM client.

## Application execution API

- `IFoundgineExecutor` is the narrow entry point for executing a `SemanticRequest` or external
  provider-neutral `ReadIntent`.
- `IFoundgine` adds capability discovery, dry-run planning, approval and approved execution.
- `IFoundgineMutations` exposes the mutation lifecycle through the runtime mutation API.
- Successful mutation execution returns the shared core `ExecutionReceipt` on
  `MutationExecutionResult.receipt()`. It records provider identity, semantic/version fingerprints,
  authorization fingerprint, affected operation nodes, effects, result fingerprint, warrant
  provenance and approval metadata without retaining domain payloads.

## Shared protocol-neutral intent boundary

`IFoundgineIntentHandler<TPrincipal>` is the application-owned boundary that HTTP, GraphQL and MCP
adapters should share. It receives a `FoundgineIntentRequest`, the authenticated host principal, and
a cancellation token, and returns a `CompletionStage<FoundgineIntentResult>`.

Read requests use `FoundgineIntentRequest` and support three operations:

- `EXECUTE` — requires a typed core `ReadIntent`.
- `EXPLAIN` — requires a typed core `ReadIntent` and must not execute provider work.
- `CAPABILITIES` — describes what the caller may discover and must not carry an intent.

Mutation requests use `FoundgineMutationIntentRequest` and share the same handler and result
contract. They wrap the existing `SemanticMutationRequest` and support `DRY_RUN`, `APPROVE`,
`EXECUTE` and `EXECUTE_APPROVED`. The handler derives the approver identity from the authenticated
principal; the request intentionally has no caller-controlled `approvedBy` field. The default
`handleMutationAsync` implementation fails closed, so an application must explicitly opt in to writes.

`MutationPlanApproval` is a trusted runtime object, not a wire format. A transport must not accept it
from caller-controlled JSON. Instead, issue opaque approval handles, store the approval server-side,
bind it to the authenticated subject/tenant and enforce expiry, one-time use, audit and revocation.

The principal is generic on purpose. The runtime does not depend on Spring Security, Jakarta Security,
servlet APIs or a particular identity provider. A host must resolve the principal from its verified
authentication mechanism and pass it separately. Never accept tenant, role, subject or authority from
intent fields. The application-owned handler selects policy, maps the host principal into its
authorization context, shapes safe result data and translates exceptions into transport-neutral
outcomes. Each protocol adapter maps those outcomes to its own response contract.

The handler interface does not bypass Foundgine's execution-time authorization. Capability discovery
is descriptive; execution must still be authorized by the configured semantic policy and provider
security gates.


## Warrant trust, revocation and delegation

Warrant-backed read and mutation discovery/planning/execution fail closed unless the runtime has a configured
`ISecurityWarrantRevocationStore`. Both the query and mutation engines verify the warrant signature and issuer,
check the requested subject/audience/tenant and capability grants, and validate current revocation
state. Immediately before provider dispatch, the query engine revalidates trust and captures a
revocation snapshot; after provider execution it rejects if the revocation sequence changed while
the operation was in flight. Successful mutation execution attaches an `ExecutionReceipt` to the
result, including the warrant ID/digest and security-invariant digest where applicable. The receipt
is evidence of the completed operation, not a replacement for the host's durable audit log. The
mutation engine additionally consumes its warrant replay key only
after the final pre-dispatch trust and revocation checks pass, then performs the same post-execution
revocation check.

Delegated warrants additionally require an explicitly configured root issuer, the complete
root-to-leaf chain on `SecurityExecutionContext`, and an `ISecurityWarrantDelegationTrustResolver`.
Every signature and delegation edge is revalidated; child constraints and grants cannot exceed the
parent's authority. Supply the chain using `withDelegationChain(...)`; the context copies it into an
immutable list. A caller-supplied chain is evidence to verify, not authority by itself.

## Mutation audit and receipt correlation

`FoundgineMutationEngine` accepts an `IAuditLog` in its extended constructor. The compatibility
constructors use an in-memory log; production hosts should inject `BufferedDurableAuditLog` backed
by an `AuditEventStore`, wrapped in `HashChainedAuditLog` when they need append ordering and
verification. Mutation approval, pre-execution denial, provider failure and successful execution
produce metadata-only audit events. Success events use the same receipt request ID as their
correlation ID; raw request and result payloads are not copied into the audit log.

`HashChainedAuditLog.verify(events)` checks contiguous sequence numbers, predecessor links and
SHA-256 event hashes. Verify the complete unfiltered stream: filtered queries omit chain links and
are not independently verifiable. A plain hash chain detects partial edits and reordering but is
not a keyed signature; protect the chain head and durable store, and use external signing or
immutable storage when privileged-administrator tampering is in scope.

If the provider has completed a mutation but the success audit append fails, the engine throws
`MutationAuditPersistenceException`, which carries the completed `MutationExecutionResult` and
receipt. This is an ambiguous acknowledgement, not a failed mutation: reconcile using the receipt
request ID and do not blindly retry.

The Java fluent options mirror the C# hosting options:

```java
new FoundgineOptions()
    .warrantKeyResolver(keyResolver)
    .expectedWarrantIssuer("trusted-root-issuer")
    .warrantReplayStore(replayStore)
    .warrantRevocationStore(revocationStore)
    .warrantDelegationTrustResolver(delegationTrustResolver);
```

`HighAssurance` supplies development defaults for key verification, replay protection and an
in-memory revocation store. Those defaults are process-local and are not production persistence.
Production hosts should provide durable implementations and configure a delegation trust resolver
when delegated warrants are accepted. Do not omit revocation state in production: the mutation
engine refuses warrant-backed work when it is unavailable.


### Correlation and read audit events

`ExecutionContextKeys.CORRELATION_ID` carries a host-supplied opaque correlation value through provider execution. The value is not an identity, tenant, authorization claim or idempotency key. `ExecutionReceipt.requestId` remains a unique execution identifier; `ExecutionReceipt.correlationId` links the execution to a transport/request trace. The old receipt constructor and factory overloads remain source-compatible.

`FoundgineOptions.auditLog(IAuditLog)` configures the read-execution audit sink; the default is process-local `InMemoryAuditLog`. Successful reads, provider failures and pre-dispatch denials are recorded as `READ_SUCCEEDED`, `READ_FAILED` and `READ_DENIED`. The configured `IAuditLog` is also registered in `FoundgineServiceRegistry`. For durable evidence, supply a durable store and optionally wrap it in `HashChainedAuditLog`. Audit append failure is fail-closed for successful read acknowledgement.


For administrator-resistant anchoring, `HashChainedAuditLog.createCheckpoint(IAuditCheckpointSigner)` signs the current sequence/head pair. `IAuditCheckpointSigner` is deliberately host-owned: use a KMS/HSM-backed implementation and persist checkpoints outside the audit store. `HashChainedAuditLog.verifyCheckpoint(...)` verifies a checkpoint with the matching key. The runtime does not ship or persist signing keys. A verifier should compare the signed checkpoint with the chain at that sequence; a valid signature over a checkpoint alone does not prove the underlying event store has retained every event.
