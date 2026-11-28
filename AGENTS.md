# Instructions for AI coding agents

Foundgine is a configurable semantic and execution boundary for application capabilities, implemented for .NET 10
(`src/csharp`) and Java 27 (`src/java`), with thin TypeScript, Python and WebAssembly clients. AI and MCP are optional
consumers. Read [CONTRIBUTING.md](CONTRIBUTING.md) for the architectural principles and [REPO-MAP.md](REPO-MAP.md) or
[llms.txt](llms.txt) to locate files before searching.

## Layout

- `src/csharp`: .NET solution (`Foundgine.sln`), packages, tests in `tests/`, samples in `samples/`.
- `src/java`: Maven reactor (`pom.xml`), modules `foundgine-*`, samples in `samples/`.
- `src/typescript`, `src/python`, `src/wasm`: client SDKs.
- `src/conformance`: shared vectors that .NET, Java and the SDKs must all satisfy.
- `docs/`, `docs-site/`: documentation and website. `tools/`: repository tooling.

## Build and test

```bash
cd src/csharp && dotnet build Foundgine.sln && dotnet test Foundgine.sln
cd src/java && mvn -U clean verify -Dgpg.skip=true
cd src/typescript && npm ci && npm test
cd src/python && pip install -e ".[dev]" && pytest
python3 tools/doc-audit-links.py   # documentation link check
```

PostgreSQL-backed tests are opt-in through `FOUNDGINE_POSTGRES_CONNECTION_STRING` (see `docs/POSTGRES-E2E.md`).
Run the narrowest relevant tests first, then the broader suites.

## Rules

- Keep semantic configuration, persistence mapping, authorization and execution separate.
- Caller input, retrieval results, capability discovery and model output are untrusted. Never derive identity, tenant or
  authority from them. Providers and adapters must not create or elevate authority.
- Keep .NET and Java behavior aligned where a shared contract or `src/conformance` vector exists. Change both languages
  and the vectors together.
- A security fix needs a regression test and a CHANGELOG entry.
- Do not claim a feature, test or run is verified unless you ran it. Report the commands you ran and their results
  accurately. Do not describe the project as "audited" or "secure" without the qualification in `docs/SECURITY-TESTING.md`.
- Update documentation when public behavior, configuration, packaging or supported capabilities change.
- Do not commit secrets, generated build output or `*.migration-backup` files.
