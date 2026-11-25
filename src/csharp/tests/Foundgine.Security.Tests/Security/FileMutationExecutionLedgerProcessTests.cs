using System.Diagnostics;
using System.Reflection;
using Foundgine.Core.Execution;
using Foundgine.Runtime.Recovery;
using Xunit;

namespace Foundgine.Security.Tests.Security;

/// <summary>
/// The file ledger across real operating-system processes: exclusivity, compare-and-set, and crashed lock holders.
/// Counterpart of the Java <c>FileMutationExecutionLedgerProcessTest</c>; the child is
/// <c>Foundgine.Security.Tests.LedgerChild</c>, located through the <c>LedgerChildDll</c> assembly metadata.
/// </summary>
public sealed class FileMutationExecutionLedgerProcessTests : IDisposable
{
    private const int Processes = 4, Keys = 40, UpdatesEach = 25;
    private static readonly DateTimeOffset T0 = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fg-ledger-proc-" + Guid.NewGuid().ToString("N"));

    public FileMutationExecutionLedgerProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static MutationExecutionRecord Record(string key, string requestId)
    {
        var receipt = new ExecutionReceipt(requestId, "pending", "sm-1", 1, 1, 1, 1, "intent", "plan", "auth", "provider",
            T0, T0, [1], ["write"], "pending", CorrelationId: "corr-1");
        return new MutationExecutionRecord("t", "s", key, requestId, "plan", "corr-1", MutationExecutionState.Started, 1,
            0, T0, T0, T0.AddSeconds(60), receipt, [], false, null);
    }

    private static Process Spawn(string ledgerPath, string mode, string id)
    {
        var dll = typeof(FileMutationExecutionLedgerProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LedgerChildDll")?.Value;
        Assert.True(dll is not null && File.Exists(dll), "ledger child not built: " + dll);
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { dll!, ledgerPath, mode, id }) info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new InvalidOperationException("could not start ledger child");
    }

    private static async Task<List<string>> Output(Process p)
    {
        var lines = new List<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add("ERR " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await p.WaitForExitAsync(cts.Token);
        p.WaitForExit(); // flushes the async readers
        return lines;
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        p.WaitForExit();
    }

    [Fact]
    public async Task RacingProcessesGetOneWinnerPerKeyAndLoseNoUpdates()
    {
        var path = Path.Combine(_dir, "race.jsonl");
        var ledger = new FileMutationExecutionLedger(path);
        Assert.True((await ledger.TryCreateAsync(Record("shared", "r0"))).Created);
        var procs = Enumerable.Range(0, Processes).Select(i => Spawn(path, "race", i.ToString())).ToList();
        var winners = new Dictionary<string, int>();
        foreach (var p in procs)
        {
            var output = await Output(p);
            Assert.True(p.ExitCode == 0, string.Join("\n", output));
            Assert.True(output.Contains("DONE " + UpdatesEach), string.Join("\n", output));
            foreach (var l in output.Where(l => l.StartsWith("W ", StringComparison.Ordinal)))
                winners[l] = winners.GetValueOrDefault(l) + 1;
        }
        Assert.Equal(Keys, winners.Count);
        foreach (var (k, n) in winners) Assert.True(n == 1, k + " won more than once");
        // every compare-and-set update landed exactly once: version 1 + all updates
        var shared = await new FileMutationExecutionLedger(path).FindAsync("t", "s", "shared");
        Assert.Equal(1L + Processes * UpdatesEach, shared!.Version);
    }

    [Fact]
    public async Task SeparateInstancesInOneProcessShareTheLockSafely()
    {
        var path = Path.Combine(_dir, "same-process.jsonl");
        var won = 0;
        var tasks = new[] { new FileMutationExecutionLedger(path), new FileMutationExecutionLedger(path) }
            .Select(l => Task.Run(async () =>
            {
                for (var k = 0; k < Keys; k++)
                    if ((await l.TryCreateAsync(Record("k" + k, "r"))).Created) Interlocked.Increment(ref won);
            }));
        await Task.WhenAll(tasks);
        Assert.Equal(Keys, won);
    }

    [Fact]
    public async Task LiveHolderIsNeverEvictedAndADeadHolderBlocksNobody()
    {
        var path = Path.Combine(_dir, "hold.jsonl");
        var ledger = new FileMutationExecutionLedger(path);
        // a live holder (an exclusive open of the lock file) must make the ledger wait, not evict
        var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<MutationLedgerCreateResult> create;
        try
        {
            File.SetLastWriteTimeUtc(path + ".lock", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            create = Task.Run(async () => await ledger.TryCreateAsync(Record("k", "r")));
            await Task.Delay(1500);
            Assert.False(create.IsCompleted); // waits for a live holder even if the lock file looks old
        }
        finally { held.Dispose(); }
        Assert.True((await create.WaitAsync(TimeSpan.FromSeconds(30))).Created);
        Assert.NotNull(await ledger.FindAsync("t", "s", "k"));
        // a holder that dies releases the lock with its process: a killed writer blocks nobody
        var p = Spawn(path, "write", "x");
        await Task.Delay(500);
        Kill(p);
        Assert.True((await ledger.TryCreateAsync(Record("after-kill", "r"))).Created);
    }

    [Fact]
    public async Task KillingAWriterAtArbitraryPointsLeavesAReadableLedger()
    {
        var path = Path.Combine(_dir, "kill.jsonl");
        for (var round = 0; round < 5; round++)
        {
            var p = Spawn(path, "write", "r" + round);
            await Task.Delay(200 + round * 150);
            Kill(p);
            var reader = new FileMutationExecutionLedger(path); // fails closed on corruption; a torn tail is tolerated
            Assert.True((await reader.TryCreateAsync(Record("probe" + round, "r"))).Created);
            Assert.NotNull(await reader.FindAsync("t", "s", "probe" + round));
        }
    }
}
