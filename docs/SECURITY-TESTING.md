# Security testing

## What runs in CI today

- Red-team and injection regression tests for the Advanced Supply Chain sample, in .NET and Java
  (`tools/security/intent-boundary/README.md`).
- Dependency audit and CodeQL (`.github/workflows/security.yml`, `codeql.yml`).
- ZAP, nmap and protocol-fuzz scripts (`src/csharp/security/pentest`, `src/java/security/pentest`).

The red-team and injection suites run in-process against in-memory data. They do not exercise HTTP or a database.

## AI-agent testing

Harness: `tools/security/ai-pentest`. Two open-source agents, both run against the HTTP surface of the Advanced Supply
Chain sample in .NET and Java, with identical scope instructions:

- **Strix** (Apache-2.0), scripted with `run-strix.sh`.
- **PentAGI** (MIT), run from its UI per `PENTAGI.md`.

Status: harness in place, **runs not yet recorded**. Targets are local containers with synthetic in-memory data.

| Date | Agent and version | Language | Model | Scan mode | Findings (real / false positive) | Fixed | Regression tests |
|---|---|---|---|---|---|---|---|
| _pending_ | Strix | .NET | | | | | |
| _pending_ | Strix | Java | | | | | |
| _pending_ | PentAGI | .NET | | | | | |
| _pending_ | PentAGI | Java | | | | | |

Fill one row per run, linking each real finding to its fix and test. Agent output is triaged by hand; an agent report is
not a finding until reproduced.

## Known limitations

- The older graph overload without a contract (C# `Authorize(SemanticGraph)`, Java `authorize(SemanticGraph)`) does not
  check the filter or ordering in the graph's options; it copies them through. Confirmed in both languages. The engines
  and the EF/JPA readers do not use it (they authorize the `SemanticOperation`, which does check them), but it is public
  API. The new contract-aware overload (`Authorize(contract, graph)` / `authorize(contract, graph)`) checks them, with
  matching tests in both languages. Many existing tests (about 40 in C#, a handful in Java) still call the old overload;
  migrate them, then retire it before claiming every path is protected.
  Verification so far: the Java change compiled and its tests passed (together with the 8 existing query-option tests)
  using a JUnit stand-in and a temporary copy of the core with Jackson removed; the C# change has not been compiled or run.
- The Java parser now rejects bad input it used to accept or ignore (for example a wrong-typed `order`).
- No independent penetration test or audit has been done.

## Wording guidance

Safe once CI passes: "Red-team and injection regression tests run in CI for .NET and Java."
Safe after all four runs are recorded: "Additionally tested with the open-source Strix and PentAGI AI pentest agents
against both samples; findings were triaged and fixed." If only some runs are done, name only those. Do not write "penetration tested", "audited" or "secure" without that qualification.
