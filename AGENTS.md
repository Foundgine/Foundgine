# AGENTS.md — Foundgine

Instructions for AI coding agents and assistants (Codex, Claude Code, Cursor, Copilot, Gemini, Devin and others). Humans: see [`README.md`](README.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md).

## What this repository is

Foundgine is a configurable semantic and execution boundary for application capabilities. It separates application meaning, persistence mapping, authorization and execution from the interfaces and agents that consume them. Fluent configuration is the primary developer experience in C# and Java. AI, MCP, GraphQL and agent frameworks are optional consumers; the project is conventional application infrastructure first. License: Apache-2.0. Current version: 3.4.1 (.NET 10 and Java 27).

## Find your way around

| Need | Read |
|---|---|
| Directory map with file counts and descriptions | [`REPO-MAP.md`](REPO-MAP.md) |
| Every file with its size (JSON) | [`repo-index.json`](repo-index.json) |
| Concise overview / extended technical context | [`llms.txt`](llms.txt) / [`llms-full.md`](llms-full.md) |
| Architecture, authorization, security | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), [`docs/AUTHORIZATION.md`](docs/AUTHORIZATION.md), [`docs/SECURITY.md`](docs/SECURITY.md) |
| Feature wiki | [`docs/wiki/index.md`](docs/wiki/index.md) |
| How to read this repo with MCP servers, indexes and crawlers | [`docs/AI-ACCESS.md`](docs/AI-ACCESS.md) |

Source roots: `src/csharp` (.NET 10), `src/java` (Java 27 Maven reactor), `src/typescript`, `src/python`, `src/wasm` (thin client SDKs), `src/conformance` (shared cross-language fixtures). Samples are under `src/csharp/samples` and `src/java/samples`. The website is `docs-site/`.

## Build and test

Run the narrowest relevant check first, then broader ones.

```bash
# .NET (SDK for net10.0)
dotnet restore src/csharp/Foundgine.sln
dotnet build   src/csharp/Foundgine.sln --configuration Release --no-restore
dotnet test    src/csharp/Foundgine.sln          # or a single project under src/csharp/tests

# Java (JDK 27, Maven 3.9+) — run from src/java
cd src/java && mvn -U clean verify -Dgpg.skip=true
cd src/java && mvn -U -pl samples/foundgine-supply-chain -am verify -Dgpg.skip=true   # one sample + prerequisites

# Client SDKs
cd src/typescript && npm test                    # tsc build + node --test
cd src/python && pip install -e ".[dev]" && ruff check . && mypy && pytest
cd src/wasm && npm run build && npm test         # needs the Rust wasm32 target and wasm-bindgen

# Documentation links and the generated repo index
python tools/doc-audit-links.py
python tools/generate-repo-index.py --check
```

Java details and caveats are in [`src/java/BUILDING.md`](src/java/BUILDING.md). Some integrations (Spring Boot, LangChain4j) are documented as not runtime-certified in this snapshot; do not describe them as certified. Report the commands you ran and their real results. Do not mark a feature verified because it compiles in another module or language.

## Rules that matter

- Keep semantic configuration, persistence mapping, authorization and execution separate.
- The host application owns trusted identity, tenant context, secrets, domain invariants and policy. Caller input, retrieval, capability discovery and model output are untrusted; discovery is not authorization. Providers and interface adapters must not create or elevate authority.
- Prefer language-idiomatic fluent configuration over annotations as the primary mechanism.
- Preserve existing ORM, database and domain infrastructure. Foundgine complements them; never claim it replaces them, and never claim universal performance or security superiority.
- Do not infer parity between .NET and Java, or between providers. Verify in implementation, tests and CI.
- When public behavior, configuration, package layout or capabilities change, update the docs. Keep the message consistent across `README.md`, `llms.txt`, `llms-full.md`, `docs-site/` and `docs/wiki/`. Source-of-truth order: implementation and tests, then module READMEs, then `docs/`, then the root discovery files.
- After adding, removing or moving files, regenerate the index: `python tools/generate-repo-index.py`.

## Do not read unless needed

`docs-site/assets/agent-benchmark/run2/` holds very large raw benchmark JSON files (up to ~30 MB each; local benchmark runs write the same files to the git-ignored `src/csharp/benchmarks/AgentEndToEnd/Run2/artifacts/`). Use the summaries in `docs-site/agent-benchmark/` and `docs/performance/` instead. `repo-index.json` flags every file of 1 MB or more with `"large": true`.
