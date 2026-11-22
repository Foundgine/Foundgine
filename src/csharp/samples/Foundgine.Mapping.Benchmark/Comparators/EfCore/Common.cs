// EF Core 10 comparator. Not yet compiled against the EF Core packages; only syntax-checked.
// Shared plumbing, excluded from line counts (the Foundgine harness's shared Domain.cs is excluded the same way).
using Microsoft.EntityFrameworkCore;
namespace Mapping.Benchmark.Comparators;
public abstract class ScenarioDb(DbContextOptions options) : DbContext(options)
{
    public string TenantId { get; set; } = "";   // set per request from the caller's claims
    public int CustomerId { get; set; }          // set per request from the caller's claims
}
