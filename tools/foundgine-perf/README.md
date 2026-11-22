# foundgine-perf

Benchmark harness that closes the gaps listed in *Foundgine Performance Report* (16 Nov 2026).
Stdlib-only Python 3.10+, so it runs anywhere the targets are reachable over HTTP (GraphQL, MCP-over-HTTP, REST).

## Measurement core

| Report action | Where | Status |
|---|---|---|
| 1 Resource telemetry, no placeholder zeros | `fpbench/sampler.py` (pid, cgroup v2, docker; runs on the host). A cell with zero samples is marked invalid | pid mode verified; docker/cgroup modes written, not yet run |
| 2 Response equivalence | `loadgen.canonical` + runner probe: normalised JSON hash per request, mismatch marks the scenario non-equivalent | verified incl. negative test |
| 3 >=5 sessions, randomised interleaved order, stable-throughput warm-up, median ratio with 95% bootstrap CI | `runner.py`, `stats.py`, `report.py` | verified (3 sessions in selftest) |
| 4 Environment disclosure | `env.py` -> `environment.json`; report blocks claim-grade if incomplete | verified |
| 5 Open loop, latency from intended send time | `loadgen._open_loop`, fractions of measured capacity | verified on mocks; knee search past 64 not yet scripted |
| Little's Law gate | `runner.py` marks cells that disagree by >15% | verified |

`report.md` states **Claim grade: NO** with explicit blockers unless every gate passes.

## Scenarios and stacks

- `scenarios/gen_coffeebeanery.py` + generated `cb-*.json`: exact GraphQL operations from `CoffeeBeanery.LoadTest` (query top-50, upsert batch 1/10/50; logical ops/s).
- `configs/coffeebeanery.json`, `compose/perf.override.yml`: published ports, equal CPU/memory limits and cpusets, loader on the host. Not yet run.
- `convention/footprint.py` (results in `docs/CONVENTION-TRACK.md`), `convention/startup_meter.py` (tested on a mock only), `docs/CONVENTION-TRACK.md` (three-arm design).
- ORM pairs: arm `foundgine-ef` (Foundgine GraphQL + Foundgine EF Core provider over the same `BankingEntityContext` as Hot Chocolate + EF Core), wired into `compose/query.yml`, `compose/perf.override.yml` and `configs/coffeebeanery.json`. See `docs/ORM-PAIRS.md`. Not yet compiled or run; `tests/test_orm_pairs.py` checks wiring only. The Java pair (Spring GraphQL + Hibernate vs Foundgine + JPA provider) is planned.

## Business journeys

- `fpbench/journey.py`, `fpbench/journey_report.py`, `fpbench/journeys/foundgine_e2e.py`: open-loop business journeys (latency from intended arrival), step timing, probes, fault-injection events, sustainable-rate report.
- `configs/e2e-journeys.json`, `docs/E2E-JOURNEYS.md`: plan from report section 5 mapped to runnable cells. `selftest/run_selftest_journeys.sh` verifies the runner on a mock stack.
- The e2e example builds and passes EG4 (E2E_OK and the Temporal CustomerToCash workflow Completed) once `readiness/prepare-e2e.sh` has patched a copy of it. The ten fixes are listed in that script's header: Foundgine version pin, split Extensions packages, Flyway starter, missing `Directory.Build.props` in the CRM images, `/dev/tcp` health probes under dash, the one-shot `erp-seed` in `up --wait`, and a Keycloak realm without the `basic` scope (tokens had no `sub`). The journeys and configs have not yet been run against it.
- Staged-rate cells (`"staged": [{rate, s}, ...]` in journey configs), `fpbench check recovery` (R6 recovery time and graceful degradation) and `e2e-staged.json`; verified on the mock stack.

## Regression gate and tests

- `tests/test_fpbench.py` (unit tests: percentiles, bootstrap, Little's Law, canonical hashing, sampler zero-sample rule, gate, report grading).
- `fpbench/gate.py` + `baselines/`: regression gate (`python3 -m fpbench gate new.json baseline.json --require-claim-grade`). A committed claim-grade baseline needs a real run.
- `docs/WHY-FOUNDGINE.md`: claims register with evidence status per claim.

## Production-readiness checks

See `docs/PRODUCTION-READINESS-PLAN.md`.

- `readiness/`: `criteria.json` (R1-R22, EG1-EG6, IND1-3), `run.sh` + `stages/*.sh`, `gen_configs.py` (all e2e configs from one compose project name, 31 fault cells), `parse_results.py` (TRX/Surefire counts, skipped = reject), `prepare-e2e.sh`, `lib.sh`.
- `fpbench/readiness.py` (evidence ledger + verdict), `fpbench/checks.py` (aa, soak, faults, tenant, calibrate, perf, claim, restart), `fpbench/invariants.py` (cross-system data invariants).
- `fpbench/parity.py` + `check parity` + `readiness/stages/parity.sh` + `readiness/parity-vectors.json` (R13). Data-driven: fill the template with real REST/GraphQL/MCP requests (>= 10 vectors, 3 surfaces, no `todo`). The checker is verified on the mock only; the mock serves all three surfaces from one endpoint, so this is not evidence of parity.
- **R22** (critical): bearer-token hardening. `fpbench/authattack.py`, `check authattack`, `readiness/stages/auth-attack.sh` (needs `AUTH_ENDPOINTS`). Sends no token, garbage, bad signature, `alg: none` and edited-payload tokens to each protected endpoint; all must be 401/403, and a valid token must still work (positive control). 5xx/timeouts are inconclusive, never a pass. Verified against a local strict server (pass) and a permissive one (fail); not run against the real stack. It does not cover approval tokens/warrants (R14), which are separate.
- `readiness/stages/preflight.sh`: run `bash readiness/run.sh --stage preflight` first on the real host. It checks tools (docker daemon, dotnet, mvn, java, git, python >= 3.10), checkouts, the env vars each stage needs, free ports, >= 20 GB disk, and runs shellcheck if installed. `STAGES="faults soak"` limits it to what you plan to run. It writes no evidence.

## Status and limits

- Verified without Docker, .NET or Java: unit tests, `selftest`, `calibrate`, and bash syntax of every stage.
- Written but not yet run against a real stack: entry, e2e-up, lib-aa, lib, e2e-perf, faults, soak, tenant, ops, existing-suites, scans.
- Selftest numbers come from Python mock servers and are not Foundgine performance.
- Still open: equal-work controls (R5), cross-surface auth parity and Redis-down live tests (R13/R14), an agent attack corpus (R15); EG6, R20, R21, IND1 and IND2 are manual.
- Convention/AOT track: `Foundgine.CoffeeBeanery.BenchmarkApi` hand-writes `CoffeeBeanerySemanticModel.cs` and `CoffeeBeaneryMetadata.cs`; it does not use `Foundgine.Providers.Orm.Generator` (`[FoundgineEntity]` attributes), so current numbers do not exercise the convention path. A generator-based variant is needed, with scaffolding metrics (hand-written vs generated lines, time to first endpoint, AOT cold start, binary size, RSS).
- Also open: Temporal schedule-to-start and duplicate-delivery probes, a CRM outbox probe, and equal-work controls (real auth policy, tuned EF, raw SQL floor, EF batch tool).

## Run the selftest

`bash selftest/run_selftest.sh`

## Why earlier reports showed zero CPU/memory

`CoffeeBeanery.LoadTest/Program.cs` calls `docker stats` from inside the load-generator container, which has no Docker CLI/socket, so every read returns null and averages default to 0. The sampler in `fpbench/sampler.py` runs on the host instead.
