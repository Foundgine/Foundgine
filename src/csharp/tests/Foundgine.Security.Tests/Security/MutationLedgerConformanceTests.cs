using System.Reflection;
using System.Text;
using System.Text.Json;
using Foundgine.Runtime.Recovery;
using Xunit;

namespace Foundgine.Security.Tests.Security;

/// <summary>
/// Cross-language conformance: src/conformance/mutation-ledger-journal.jsonl is the
/// <c>foundgine-mutation-ledger-v1</c> form every runtime must read and rewrite byte for byte. The Java counterpart is
/// <c>MutationLedgerConformanceTest</c>.
/// </summary>
public sealed class MutationLedgerConformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fg-ledger-" + Guid.NewGuid().ToString("N"));

    public MutationLedgerConformanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Resource(string name)
    {
        var asm = typeof(MutationLedgerConformanceTests).Assembly;
        using var s = asm.GetManifestResourceStream("Foundgine.Security.Tests." + name)
                      ?? throw new InvalidOperationException("Missing embedded fixture " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string[] JournalLines() => Resource("mutation-ledger-journal.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private string CopyJournal()
    {
        var p = Path.Combine(_dir, "journal.jsonl");
        File.WriteAllText(p, Resource("mutation-ledger-journal.jsonl"), new UTF8Encoding(false));
        return p;
    }

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    [Fact]
    public void EveryJournalLineRoundTripsByteForByte()
    {
        var lines = JournalLines();
        Assert.NotEmpty(lines);
        foreach (var line in lines)
            Assert.Equal(line, MutationExecutionRecordJson.ToJson(MutationExecutionRecordJson.FromJson(line)));
    }

    [Fact]
    public async Task LedgerReadsTheJournalAndLatestVersionWins()
    {
        var ledger = new FileMutationExecutionLedger(CopyJournal());
        using var doc = JsonDocument.Parse(Resource("mutation-ledger-scenarios.json"));
        foreach (var e in doc.RootElement.GetProperty("finalRecords").EnumerateArray())
        {
            var r = await ledger.FindAsync(Str(e, "tenant"), Str(e, "subject"), e.GetProperty("idempotencyKey").GetString()!);
            Assert.NotNull(r);
            Assert.Equal(e.GetProperty("requestId").GetString(), r!.RequestId);
            Assert.Equal(e.GetProperty("state").GetString(), r.State.ToWireName());
            Assert.Equal(e.GetProperty("version").GetInt64(), r.Version);
            Assert.Equal(Str(e, "resolution"), r.Resolution);
            Assert.Equal(e.GetProperty("affectedRows").GetArrayLength(), r.AffectedRows.Count);
        }
        var expected = doc.RootElement.GetProperty("unresolved").EnumerateArray().Select(x => x.GetString()!).ToList();
        var actual = (await ledger.ListUnresolvedAsync(100)).Select(r => r.IdempotencyKey).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TornTailIsIgnoredAndCorruptLineFailsClosed()
    {
        using var doc = JsonDocument.Parse(Resource("mutation-ledger-scenarios.json"));
        var torn = CopyJournal();
        File.AppendAllText(torn, doc.RootElement.GetProperty("tornTail").GetProperty("append").GetString()!, new UTF8Encoding(false));
        var ledger = new FileMutationExecutionLedger(torn);
        var count = 0;
        foreach (var e in doc.RootElement.GetProperty("finalRecords").EnumerateArray())
            if (await ledger.FindAsync(Str(e, "tenant"), Str(e, "subject"), e.GetProperty("idempotencyKey").GetString()!) is not null) count++;
        Assert.Equal(doc.RootElement.GetProperty("finalRecords").GetArrayLength(), count);

        var lines = JournalLines();
        var corruptSpec = doc.RootElement.GetProperty("corruptLine");
        lines[corruptSpec.GetProperty("replaceLine").GetInt32()] = corruptSpec.GetProperty("with").GetString()!;
        var corrupt = Path.Combine(_dir, "corrupt.jsonl");
        File.WriteAllText(corrupt, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        var bad = new FileMutationExecutionLedger(corrupt);
        await Assert.ThrowsAsync<MutationLedgerException>(async () => await bad.FindAsync("t1", "s1", "k-started"));
    }

    [Fact]
    public async Task CreateIsExclusiveAndUpdateIsCompareAndSetAcrossInstances()
    {
        var path = Path.Combine(_dir, "l.jsonl");
        var a = new FileMutationExecutionLedger(path);
        var b = new FileMutationExecutionLedger(path);
        var seed = MutationExecutionRecordJson.FromJson(JournalLines()[0]).WithVersion(0);
        var c1 = await a.TryCreateAsync(seed);
        Assert.True(c1.Created);
        Assert.Equal(1, c1.Record.Version);
        var c2 = await b.TryCreateAsync(seed);
        Assert.False(c2.Created);
        Assert.Null(await b.TryUpdateAsync(99, c1.Record));
        var stored = await b.TryUpdateAsync(1, c1.Record);
        Assert.Equal(2, stored!.Version);
        Assert.Null(await a.TryUpdateAsync(1, c1.Record));
        Assert.Equal(2, (await a.FindAsync(seed.Tenant, seed.Subject, seed.IdempotencyKey))!.Version);
    }

    private static MutationExecutionRecord WithResolution(string resolution)
    {
        var line = JournalLines()[0];
        Assert.Contains("\"resolution\":null", line);
        return MutationExecutionRecordJson.FromJson(
            line.Replace("\"resolution\":null", "\"resolution\":" + JsonSerializer.Serialize(resolution), StringComparison.Ordinal));
    }

    [Fact]
    public void StringEscapingMatchesJacksonDefaults()
    {
        // Jackson (Java) escapes only '"', '\' and control characters below U+0020 (upper-case \u00XX); all other text
        // is raw UTF-8. These are the exact literals the Java runtime writes.
        var cases = new (string Value, string Literal)[]
        {
            ("plain", "\"plain\""),
            ("caf\u00e9 \u65e5\u672c", "\"caf\u00e9 \u65e5\u672c\""),
            ("emoji \U0001F600", "\"emoji \U0001F600\""),
            ("a<b>&'+c/d", "\"a<b>&'+c/d\""),
            ("q\"b\\s", "\"q\\\"b\\\\s\""),
            ("\b\t\n\f\r", "\"\\b\\t\\n\\f\\r\""),
            ("\u0000\u0001\u001f", "\"\\u0000\\u0001\\u001F\""),
            ("\u007f\u0085\u00a0\u2028\u2029", "\"\u007f\u0085\u00a0\u2028\u2029\""),
        };
        foreach (var (value, literal) in cases)
        {
            var json = MutationExecutionRecordJson.ToJson(WithResolution(value));
            Assert.Contains("\"resolution\":" + literal + ",\"receipt\"", json);
            Assert.Equal(value, MutationExecutionRecordJson.FromJson(json).Resolution);
        }
    }

    [Fact]
    public async Task FileLedgerPreservesNonAsciiTextAndWritesRawUtf8()
    {
        var path = Path.Combine(_dir, "unicode.jsonl");
        const string text = "caf\u00e9 \U0001F600 line\u2028sep \u0085 tab\t\"q\"";
        var seed = WithResolution(text);
        var created = await new FileMutationExecutionLedger(path).TryCreateAsync(seed);
        Assert.True(created.Created);
        var read = await new FileMutationExecutionLedger(path).FindAsync(seed.Tenant, seed.Subject, seed.IdempotencyKey);
        Assert.Equal(text, read!.Resolution);
        Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf("\U0001F600"u8) >= 0);
    }
}
