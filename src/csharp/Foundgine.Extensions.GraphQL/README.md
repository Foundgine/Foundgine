# Foundgine.Extensions.GraphQL.HotChocolate

`Foundgine.Extensions.GraphQL.HotChocolate` is the query-side Hot Chocolate GraphQL adapter for Foundgine.

## What is in this package

- `GraphQLSchemaAdapter` — exposes/bridges Foundgine semantic schema information.
- `GraphQLSchemaDescriptor` — schema description support.
- `HotChocolateSemanticAdapter` — converts GraphQL selections into Foundgine semantic requests.
- `GraphQLVariableCoercer` — variable/value coercion at the GraphQL boundary.
- `GraphQLDirectiveEvaluator` — directive handling.
- `GraphQLResultShape` — GraphQL result-shape information.
- `GraphQLAdapterError` — adapter-level errors.

## Boundary

```text
GraphQL selection
      ↓
Foundgine.Extensions.GraphQL.HotChocolate
      ↓
provider-independent semantic intent
      ↓
Foundgine runtime
```

This package translates GraphQL. It does not itself implement the secure Foundgine execution boundary.

For secure query execution, add `Foundgine.Extensions.GraphQL.HotChocolate.HotChocolate.Execution`.

## Install

```bash
dotnet add package Foundgine.Extensions   # includes this integration
```

## HTTP endpoint

`MapFoundgineGraphQL` maps a bearer-authenticated `POST /graphql` onto the secure query (and optionally mutation) executors. It fails closed: list every query root field and the roles allowed to start there, and add an `Authorize` rule for anything claim-based.

```csharp
app.MapFoundgineGraphQL(o =>
{
    o.QueryRootRoles["order"] = ["manager", "customer"];
    o.QueryRootRoles["orderLine"] = ["manager"];   // customers read lines only through their orders
    o.Authorize = ctx => ctx.User.IsInRole("customer") && ctx.User.FindFirst("customer_id") is null
        ? new GraphQLAdapterError("FORBIDDEN", "A customer_id claim is required.", Category: "FORBIDDEN")
        : null;
    // o.AllowMutations = true;   // needs a registered FoundgineGraphQLMutationExecutor
});
```

A missing or invalid token returns HTTP 401; an authenticated caller who is not permitted gets a GraphQL error. Claims used for authorization must come from the validated token, never from a GraphQL argument. Requires `using Foundgine.Extensions.GraphQL.Http;`.

## Open intent resolver

`FoundgineIntentGraphQLResolver` is a reusable Hot Chocolate query type. Register it with `AddGraphQLServer().AddQueryType<FoundgineIntentGraphQLResolver>()`. It exposes the `foundgineIntent(intent: Any, mode: String)` field and delegates to the application's `IFoundgineIntentHandler`.

The resolver obtains the authenticated `ClaimsPrincipal` through `IFoundgineIntentPrincipalProvider`; it never accepts caller, tenant, role, warrant, or authorization predicates as GraphQL arguments. For an ASP.NET Core host, register `AddFoundgineIntentHttpPrincipal()`. Non-HTTP hosts can supply their own principal provider.

```graphql
query Intent($intent: Any!) {
  foundgineIntent(intent: $intent, mode: "execute")
}
```

The application handler performs semantic resolution and authorization. GraphQL translates the result into the GraphQL response and does not implement a second planner or policy.

ASP.NET Core registration:

```csharp
services.AddFoundgineIntentHttpPrincipal();
services.AddGraphQLServer().AddQueryType<FoundgineIntentGraphQLResolver>();
app.MapGraphQL("/graphql");
```
