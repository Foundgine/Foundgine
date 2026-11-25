using System.Globalization;
using System.Text;
using System.Text.Json;
using Foundgine.Core.Execution;

namespace Foundgine.Runtime.Recovery;

/// <summary>
/// Canonical JSON form of a <see cref="MutationExecutionRecord"/> (<c>foundgine-mutation-ledger-v1</c>). One record is
/// one line in the file ledger. The .NET and Java runtimes read and write the same form: camelCase names in a fixed
/// order, compact separators, UTC timestamps as <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> (100 ns, the .NET tick), nulls
/// written as null, no payloads.
/// </summary>
public static class MutationExecutionRecordJson
{
    public const string Format = "foundgine-mutation-ledger-v1";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
    };

    public static string? FormatTimestamp(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static string ToJson(MutationExecutionRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            Str(w, "format", Format);
            Text(w, "tenant", r.Tenant);
            Text(w, "subject", r.Subject);
            Str(w, "idempotencyKey", r.IdempotencyKey);
            Str(w, "requestId", r.RequestId);
            Str(w, "planFingerprint", r.PlanFingerprint);
            Text(w, "correlationId", r.CorrelationId);
            Str(w, "state", r.State.ToWireName());
            w.WriteNumber("attempt", r.Attempt);
            w.WriteNumber("version", r.Version);
            Str(w, "startedAtUtc", FormatTimestamp(r.StartedAt));
            Str(w, "updatedAtUtc", FormatTimestamp(r.UpdatedAt));
            Text(w, "leaseExpiresAtUtc", FormatTimestamp(r.LeaseExpiresAt));
            w.WriteStartArray("affectedRows");
            foreach (var row in r.AffectedRows) w.WriteNumberValue(row);
            w.WriteEndArray();
            w.WriteBoolean("auditAcknowledged", r.AuditAcknowledged);
            Text(w, "resolution", r.Resolution);
            w.WritePropertyName("receipt");
            WriteReceipt(w, r.Receipt);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static MutationExecutionRecord FromJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new ArgumentException("Ledger record is not valid JSON.", e); }
        using (doc)
        {
            var n = doc.RootElement;
            if (n.ValueKind != JsonValueKind.Object || Text(n, "format") != Format)
                throw new ArgumentException("Unsupported ledger record format.");
            var rows = new List<int>();
            if (n.TryGetProperty("affectedRows", out var rowsNode) && rowsNode.ValueKind == JsonValueKind.Array)
                foreach (var x in rowsNode.EnumerateArray()) rows.Add(x.GetInt32());
            return new MutationExecutionRecord(
                Text(n, "tenant"), Text(n, "subject"), Required(n, "idempotencyKey"), Required(n, "requestId"),
                Required(n, "planFingerprint"), Text(n, "correlationId"),
                MutationExecutionStateExtensions.FromWireName(Required(n, "state")), Int(n, "attempt"),
                Long(n, "version"), Instant(Required(n, "startedAtUtc")), Instant(Required(n, "updatedAtUtc")),
                InstantOrNull(Text(n, "leaseExpiresAtUtc")), ReadReceipt(n.TryGetProperty("receipt", out var rc) ? rc : default),
                rows, Bool(n, "auditAcknowledged"), Text(n, "resolution"));
        }
    }

    private static void WriteReceipt(Utf8JsonWriter w, ExecutionReceipt r)
    {
        w.WriteStartObject();
        Str(w, "requestId", r.RequestId);
        Str(w, "status", r.Status);
        Str(w, "semanticModelVersion", r.SemanticModelVersion);
        w.WriteNumber("capabilityContractVersion", r.CapabilityContractVersion);
        w.WriteNumber("capabilityVersion", r.CapabilityVersion);
        w.WriteNumber("intentVersion", r.IntentVersion);
        w.WriteNumber("planVersion", r.PlanVersion);
        Str(w, "intentFingerprint", r.IntentFingerprint);
        Str(w, "planFingerprint", r.PlanFingerprint);
        Str(w, "authorizationFingerprint", r.AuthorizationFingerprint);
        Str(w, "provider", r.Provider);
        Str(w, "startedAtUtc", FormatTimestamp(r.StartedAt));
        Str(w, "completedAtUtc", FormatTimestamp(r.CompletedAt));
        w.WriteStartArray("affectedNodeIds");
        foreach (var id in r.AffectedNodeIds) w.WriteNumberValue(id);
        w.WriteEndArray();
        w.WriteStartArray("effects");
        foreach (var e in r.Effects) w.WriteRawValue(Quote(e), skipInputValidation: true);
        w.WriteEndArray();
        Str(w, "resultFingerprint", r.ResultFingerprint);
        Text(w, "approvalId", r.ApprovalId);
        Text(w, "approvedBy", r.ApprovedBy);
        Text(w, "approvedAtUtc", FormatTimestamp(r.ApprovedAt));
        Text(w, "warrantId", r.WarrantId);
        Text(w, "warrantDigest", r.WarrantDigest);
        Text(w, "securityInvariantDigest", r.SecurityInvariantDigest);
        Text(w, "correlationId", r.CorrelationId);
        w.WriteEndObject();
    }

    private static ExecutionReceipt ReadReceipt(JsonElement n)
    {
        if (n.ValueKind != JsonValueKind.Object) throw new ArgumentException("Ledger record has no receipt.");
        var nodes = new List<int>();
        if (n.TryGetProperty("affectedNodeIds", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray()) nodes.Add(x.GetInt32());
        var effects = new List<string>();
        if (n.TryGetProperty("effects", out var e) && e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) effects.Add(x.GetString() ?? "");
        return new ExecutionReceipt(
            Required(n, "requestId"), Required(n, "status"), Required(n, "semanticModelVersion"),
            Int(n, "capabilityContractVersion"), Int(n, "capabilityVersion"), Int(n, "intentVersion"),
            Int(n, "planVersion"), Required(n, "intentFingerprint"), Required(n, "planFingerprint"),
            Required(n, "authorizationFingerprint"), Required(n, "provider"), Instant(Required(n, "startedAtUtc")),
            Instant(Required(n, "completedAtUtc")), nodes, effects, Required(n, "resultFingerprint"),
            Text(n, "approvalId"), Text(n, "approvedBy"), InstantOrNull(Text(n, "approvedAtUtc")), Text(n, "warrantId"),
            Text(n, "warrantDigest"), Text(n, "securityInvariantDigest"), Text(n, "correlationId"));
    }

    private static void Text(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else Str(w, name, value);
    }

    private static string? Text(JsonElement n, string name)
        => n.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static string Required(JsonElement n, string name)
        => Text(n, name) ?? throw new ArgumentException($"Ledger record is missing '{name}'.");

    private static int Int(JsonElement n, string name) => n.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
    private static long Long(JsonElement n, string name) => n.TryGetProperty(name, out var v) ? v.GetInt64() : 0;
    private static bool Bool(JsonElement n, string name) => n.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset Instant(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? InstantOrNull(string? value) => value is null ? null : Instant(value);

    // Strings are escaped by hand to match Jackson's default generator byte for byte: only '"', '\\' and control
    // characters below U+0020 are escaped (short forms \b \t \n \f \r, otherwise upper-case \u00XX); everything else,
    // including non-BMP text, U+007F, U+0085, U+00A0 and U+2028/2029, is written as raw UTF-8.
    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static void Str(Utf8JsonWriter w, string name, string? value)
    {
        w.WritePropertyName(name);
        if (value is null) w.WriteNullValue(); else w.WriteRawValue(Quote(value), skipInputValidation: true);
    }
}
