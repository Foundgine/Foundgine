# Foundgine Java Gateway

This package holds the Java 27 gateway and provider layer in `foundgine-providers`: discovery, provider lifecycle, approvals, event journal and replay, audit hooks, and the HTTP/SSE routes.

## Included

- Immutable gateway discovery contracts and facade (`IFoundgineGateway`, `FoundgineGateway`).
- Capability catalog with case-insensitive duplicate detection.
- Provider identity and allocation-light `long` capability flags.
- Provider registry with health reporting, deterministic snapshots and lifecycle publication.
- Rollback of registration, health and unregistration mutations when lifecycle publication fails.
- Provider capability negotiation. A single provider must satisfy each complete requirement; capabilities are never combined across providers.
- JUnit parity tests for discovery, registration, rollback and negotiation.
- Opaque, encrypted mutation approval handles (`approvals/`): `FoundgineGatewayApprovalService`, an in-memory store for development and `PostgresFoundgineGatewayApprovalStore` as an atomic, durable store.
- An event journal with replay cursors and a live event hub (`events/`): `IFoundgineGatewayEventStore`, an in-memory default and `PostgresGatewayEventStore` (package `com.foundgine.providers.eventstore`) for a durable, ordered journal.
- A critical audit hook and sink contract (`audit/`).
- HTTP/SSE routes (`http/`, `FoundgineGatewayRoutes`) served through a router and the JDK HTTP server: `GET /capabilities`, `/capabilities/{id}`, `/events`, `/semantic`, `/executions/{id}` and `POST /read`, `/mutations/dry-run`, `/mutations/approvals`, `/mutations/approvals/{handle}/execute`, plus `DELETE /mutations/approvals/{handle}`. Gateway routes require authorization by default.

## Example

```java
var gatewayOptions = new FoundgineGatewayOptions()
    .name("Supply Chain Gateway")
    .contractVersion("1.0")
    .addCapability(new FoundgineGatewayCapability(
        "orders.read", "Order read", "1.0", "query",
        "Read order data", List.of("get", "list"), Map.of()));

IFoundgineGateway gateway = new FoundgineGateway(gatewayOptions);
var discovery = gateway.describe();

IFoundgineGatewayEventPublisher events = (type, subject, correlationId, providerId, attributes) -> {
    // Connect to the application's event pipeline. This defines the stable contract.
};
var registry = new FoundgineGatewayProviderRegistry(events);
registry.register(new FoundgineGatewayProviderDescriptor(
    "foundgine.postgres.eventstore", "PostgreSQL Event Store", "1.0",
    List.of("DURABLE_STORAGE", "ORDERED_EVENTS")));
registry.reportHealth("foundgine.postgres.eventstore",
    FoundgineGatewayProviderHealth.HEALTHY, "Ready");
```

Provider negotiation is explicit and does not grant execution authority:

```java
var requirement = new FoundgineProviderRequirement(
    FoundgineProviderKind.EVENT_STORE,
    FoundgineProviderCapability.mask(
        FoundgineProviderCapability.DURABLE_STORAGE,
        FoundgineProviderCapability.ORDERED_EVENTS),
    "The configured journal must be durable and ordered");

FoundgineProviderNegotiator.negotiate(providers, List.of(requirement))
    .throwIfUnsatisfied();
```

## Deliberate boundaries

The `IFoundgineGatewayEventPublisher` contract connects the gateway to an application's event pipeline; the in-memory event store and approval store are development/single-instance only, so production deployments should use the PostgreSQL stores or their own durable implementations. The registry is discovery/lifecycle metadata only and must not be treated as an authorization decision or a substitute for tenant-scoped execution checks. The execution journal is operational status, not authoritative audit evidence; keep the critical audit sink as the authoritative record. Authorization policy stays with the host application's trusted security context.

## Verification

Production sources compile with `javac --release 27`. Run the Maven/JUnit suite for this module to verify it.
