# Foundgine Supply Chain Advanced Benchmark

This is a standalone PostgreSQL-backed MCP benchmark host. It is a sibling of,
not a subproject of, `Foundgine.SupplyChain.Advanced`. It owns its host, Dockerfile,
Compose configuration, and benchmark execution setup.

## Build

From the repository root:

```bash
dotnet build src/csharp/samples/Foundgine.SupplyChain.Advanced.Benchmark/Foundgine.SupplyChain.Advanced.Benchmark.sln -c Release
```

## Run

From this directory:

```bash
docker compose up --build
```

The host exposes MCP at `/mcp`, REST at `/api/v1/supply-chain`, and GraphQL at `/graphql`. It is exposed on port `4422`; PostgreSQL is exposed on port `4429`.
The Advanced sample's `run-supply-chain.ps1` script points to this Compose file.


See [`MCP.Foundgine/Api/README.md`](MCP.Foundgine/Api/README.md) for REST/GraphQL credential handling and the shared application-service boundary.
