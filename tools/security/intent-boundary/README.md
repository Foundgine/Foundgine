# Intent-boundary security testing (Supply Chain advanced sample)

Why this exists: scanners such as **sqlmap** assume a request parameter ends up inside a SQL string. Foundgine
callers send structured *intent*, so hostile text can only be a **value** (must stay data) or a **name**
(entity, field, relationship; must be resolved against the semantic model or refused). sqlmap alone therefore
cannot tell you much here. This folder pairs two things:

1. **Two in-process test suites** in both samples: a red-team suite for authenticated low-privilege attacks, and a
   corpus suite that runs one shared payload set through the intent and asserts the boundary invariants.
2. **A localhost-only sqlmap runner** for the sample's HTTP endpoint, as a black-box cross-check.

## 1. The test suites

```bash
# .NET (from src/csharp)
dotnet test samples/Foundgine.SupplyChain.Advanced/Semantic/Tests/Foundgine.SupplyChain.Advanced.Tests.csproj --filter "FullyQualifiedName~IntentRedTeamPenetrationTests"
dotnet test samples/Foundgine.SupplyChain.Advanced/Semantic/Tests/Foundgine.SupplyChain.Advanced.Tests.csproj --filter "FullyQualifiedName~IntentInjectionCorpusTests"

# Java (from src/java)
mvn -pl samples/foundgine-supply-chain-advanced -am test "-Dtest=IntentRedTeamPenetrationTest" "-Dsurefire.failIfNoSpecifiedTests=false"
mvn -pl samples/foundgine-supply-chain-advanced -am test "-Dtest=IntentInjectionCorpusTest" "-Dsurefire.failIfNoSpecifiedTests=false"
```

### Red-team suite

| Attack | Expected result |
|---|---|
| Filter on an unreadable field, directly or nested in AND/OR | 403 |
| Order by an unreadable field | 403 |
| Filter through an unreadable relationship, or `explain` such a query | 403 |
| Wrong JSON types, trailing documents, serializer metadata keys | 400 or 403, never coerced or ignored |
| Malformed input | 400 or 403, with no parser or runtime internals in the message |
| Ambiguous tenant or role headers, identity in the body | refused |
| 400 concurrent requests across tenants and roles | each caller gets only its own result |

The filtering and ordering probes include a positive control: an allowed role gets an answer that depends on the data,
so a probe cannot pass merely because the query is invalid.

### Injection corpus suite

Both use the same 29-entry `VALUE_PAYLOADS` / `ValuePayloadList`. Keep them identical.

| Invariant | Tests |
|---|---|
| A payload never causes an unclassified failure (HTTP 500) or leaks internals | every payload test, via `assertClassified` |
| Values stay data, never a pattern or query | `stringFilterValueIsDataNotQuery` |
| A payload never widens a result past the caller's tenant | `tenantBoundaryHoldsForEqPayloads`, `tautologyViaOrNeverEscapesTheTenantPredicate`, `everyRoleStaysInsideItsTenant...` |
| A payload never turns a denial into success | `deniedRoleStaysDeniedWhateverTheValue` |
| Hostile names, operators, directions and kinds are refused | `hostileNamesAreRefusedInEverySlot`, `hostileOperatorAndDirectionAreRefused` |
| Identity never comes from the body or from hostile headers | `identityShapedPropertiesAreRejected...`, `hostileIdentityHeadersAreRefused` |
| Malformed and oversized intents fail closed (parser limits) | `malformedAndOversizedIntentsFailClosed` |
| Filtering on an unreadable field is not a side channel | `filteringOnAFieldTheCallerCannotReadDoesNotLeakItsValue` |
| The corpus changes nothing | `canaryReadsAreUnchangedAfterTheWholeCorpusHasRun` |

### What these tests do not cover

* HTTP transport, authentication middleware, SQL providers and databases.
* C#: the older `SemanticAuthorizer.Authorize(SemanticGraph)` overload does not check filters or ordering. The engine
  and the EF reader authorize a `SemanticOperation` with a contract, which does.
* C#: the in-memory provider does not implement relationship filters, so a role allowed to use one gets a 500.
* They are regression tests written alongside the code, not an independent penetration test.

Neither suite touches SQL text. The sample's in-memory provider cannot show it. SQL parameterization is covered by
`src/csharp/tests/Foundgine.E2E.Tests/Security/SqlSecurityConformanceTests.cs` and the Postgres benchmark host.

## 2. sqlmap runner (black box)

```bash
# terminal 1: start the Java sample (localhost:4783, POST /intent)
# terminal 2:
./tools/security/intent-boundary/run-sqlmap.sh                 # defaults: tenant-a, analyst, level 5, risk 3
ROLE=warehouse_operator ./tools/security/intent-boundary/run-sqlmap.sh
```

* The script **refuses any non-loopback target**, checks the baseline request returns 200, and treats 400/403 as
  expected (`--ignore-code 400,403`). Only run it against a sample you started yourself.
* Injection points (`*`) are the filter value, the order direction and the limit. Names are covered by the test
  suite, since sqlmap cannot meaningfully mutate them.
* The C# sample uses a different route and identity mechanism (see its README); set `INTENT_PATH` and edit the
  headers in `request-template.txt`.

### How to read a clean result

"All tested parameters do not appear to be injectable" is **weak evidence** here: against the in-memory provider
there is no SQL to inject into, and the semantic layer refuses most payloads before planning, so sqlmap sees
uniform 400/403 responses and has nothing to compare. The meaningful claims come from the test suite
(boundary invariants) and the SQL conformance tests (parameterization). Use sqlmap against the Postgres-backed
benchmark host if you want a realistic SQL target, and use Burp Suite for what this architecture actually
promises: replay as another tenant or role, change object ids, and confirm a denied operation leaves data unchanged.
