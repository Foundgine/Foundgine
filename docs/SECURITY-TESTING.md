# Security testing

## What runs in CI today

Every security test runs on every build, and none can skip silently.

- **.NET** (`.github/workflows/csharp-build.yml`): eight jobs feed `security-penetration-gate`: GraphQL and MCP
  penetration tests over HTTP against seeded PostgreSQL, authorization penetration, adversarial, semantic boundary,
  intent red team and injection corpus, the full security sweep, and live MCP protocol fuzzing.
- **Java** (`.github/workflows/java-build.yml`): six jobs feed `java-security-penetration-gate`: semantic boundary,
  adversarial, penetration, intent red team and injection corpus, live MCP protocol fuzzing, and the PostgreSQL + Redis sweep.
- Dependency audit, SBOM and CodeQL (`security.yml`, `codeql.yml`).

### The skip guard

Several suites skip themselves when a service or opt-in variable is missing (`FOUNDGINE_POSTGRES_CONNECTION_STRING`,
`FOUNDGINE_REDIS`, `FOUNDGINE_SUPPLYCHAIN_PENTEST`, and others). A skipped test reports as green, so a missing
variable once hid real suites: the GraphQL/MCP PenTest cases, the PostgreSQL E2E and event-store tests, and the Java
PostgreSQL and Redis tests.

Two measures close that gap:

1. The sweep jobs (`security-full-sweep`, `java-postgres-e2e`) start PostgreSQL and Redis, set every variable, and run
   every test project.
2. `tools/ci/verify_security_tests.py` then reads the TRX or JUnit reports. It discovers security tests from the
   source (file, project or class name matching penetration, red team, adversarial, security, authorization, warrant,
   replay, tamper, injection and similar) and fails the build when a security test was skipped, or when a security
   test file was run by no job. It needs no hand-kept list, so a new security test is covered when it is committed.
   A test that truly cannot run on a CI runner must be listed with a reason in `tools/ci/security-test-exemptions.txt`;
   the file starts empty and every entry is printed in the log.

Not in the merge gate: the ZAP baseline (weekly, informational), nmap and the Java ZAP script (run by hand), and the
AI agents below (they need your own LLM key).

The red-team and injection suites run in-process against in-memory data. The HTTP PenTest sample and the PostgreSQL
sweeps cover the transport and database layers.

## AI-agent testing

Harness: `tools/security/ai-pentest`. Two open-source agents, both run against the HTTP surface of the Advanced Supply
Chain sample in .NET and Java, with identical scope instructions:

- **Strix** (Apache-2.0), scripted with `run-strix.sh`.
- **PentAGI** (MIT), run from its UI per `PENTAGI.md`.

Status: all four runs execute and pass. Targets are local containers with synthetic in-memory data. Run details
(agent version, model, scan mode) are recorded per run below; complete any blank cell when you re-run.

| Date | Agent and version | Language | Model | Scan mode | Findings (real / false positive) | Fixed | Regression tests |
|---|---|---|---|---|---|---|---|
| 2026-11-28 | Strix | .NET | | | Passed, no open findings | n/a | n/a |
| 2026-11-28 | Strix | Java | | | Passed, no open findings | n/a | n/a |
| 2026-11-28 | PentAGI | .NET | | | Passed, no open findings | n/a | n/a |
| 2026-11-28 | PentAGI | Java | | | Passed, no open findings | n/a | n/a |

Add one row per run, linking each real finding to its fix and test. Agent output is triaged by hand; an agent report is
not a finding until reproduced.

## Known limitations

- The older graph overload without a contract (C# `Authorize(SemanticGraph)`, Java `authorize(SemanticGraph)`) does not
  check the filter or ordering in the graph's options; it copies them through. Confirmed in both languages. The engines
  and the EF/JPA readers do not use it (they authorize the `SemanticOperation`, which does check them), but it is public
  API. The new contract-aware overload (`Authorize(contract, graph)` / `authorize(contract, graph)`) checks them, with
  matching tests in both languages. Many existing tests (about 40 in C#, a handful in Java) still call the old overload;
  migrate them, then retire it before claiming every path is protected.
  Verification so far: the Java change compiled and its tests passed (together with the 8 existing query-option tests)
  using a JUnit stand-in and a temporary copy of the core with Jackson removed; the C# change was then built and its tests, including the PostgreSQL end-to-end suites against a PostgreSQL container, passed on 28 November 2026.
- The Java parser now rejects bad input it used to accept or ignore (for example a wrong-typed `order`).
- No independent penetration test or audit has been done.

## Wording guidance

Safe once the new pipeline has run green: "Red-team and injection regression tests run in CI for .NET and Java, and the build fails if any security test is skipped."
Safe after all four runs are recorded: "Additionally tested with the open-source Strix and PentAGI AI pentest agents
against both samples; findings were triaged and fixed." If only some runs are done, name only those. Do not write "penetration tested", "audited" or "secure" without that qualification.
