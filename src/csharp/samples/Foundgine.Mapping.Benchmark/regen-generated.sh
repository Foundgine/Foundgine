#!/usr/bin/env bash
# Regenerates Generated/ScenarioMappings.g.cs from the fluent-built scenario models, then rebuilds and runs the checks.
set -euo pipefail
cd "$(dirname "$0")"
# The harness references ScenarioMappings.All directly, so the first build needs a file to exist.
if [ ! -f Generated/ScenarioMappings.g.cs ]; then
  mkdir -p Generated
  cat > Generated/ScenarioMappings.g.cs <<'STUB'
// Bootstrap placeholder; overwritten by the emit step below.
using System.Collections.Generic;
using Foundgine.Core.Mapping;
namespace Mapping.Benchmark.Generated;
public static class ScenarioMappings
{
    public static readonly IReadOnlyDictionary<string, System.Func<MappingModel>> All = new Dictionary<string, System.Func<MappingModel>>();
}
STUB
fi
dotnet build -c Release -v q
dotnet run -c Release --no-build -- emit Generated/ScenarioMappings.g.cs
dotnet build -c Release -v q
dotnet run -c Release --no-build
