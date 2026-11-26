using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security;
using System.Text;
using System.Text.Json;
using Foundgine.SupplyChain.Advanced.Authorization;
using Foundgine.SupplyChain.Advanced.Intent;

namespace Foundgine.SupplyChain.Advanced.Mutation;

/// <summary>One authenticated application boundary for direct, dry-run, approve and execute-approved mutations.</summary>
public sealed class SupplyChainMutationDispatcher
{
    public const int MaxMutationBytes = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SupplyChainMutationStore _store;
    private readonly MutationApprovalHandleStore _approvals;
    private readonly ConcurrentQueue<MutationAuditEntry> _audit = new();
    private readonly PlaceOrderService _place;
    private readonly CancelOrderService _cancel;

    public SupplyChainMutationDispatcher(SupplyChainMutationStore? store = null, MutationApprovalHandleStore? approvals = null)
    {
        _store = store ?? new SupplyChainMutationStore();
        _approvals = approvals ?? new MutationApprovalHandleStore();
        _place = new(_store);
        _cancel = new(_store);
    }

    public IReadOnlyList<MutationAuditEntry> Audit => _audit.ToArray();

    public async Task<MutationDispatchResult> DispatchJsonAsync(string json, ClaimsPrincipal principal,
        string? correlationId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var trace = Guid.NewGuid().ToString("N");
        string subject = "unknown", tenant = "unknown", role = "unknown", tool = "unknown";
        try
        {
            trace = NormalizeCorrelation(correlationId ?? trace);
            if (Encoding.UTF8.GetByteCount(json) > MaxMutationBytes)
                throw new ArgumentException($"A mutation may not exceed {MaxMutationBytes} bytes.");
            var caller = IntentCaller.Resolve(principal, new HashSet<string>(["tenant-a", "tenant-b"], StringComparer.Ordinal));
            subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? principal.Identity?.Name ?? throw new UnauthorizedAccessException("Authenticated subject is required.");
            tenant = caller.TenantId;
            role = caller.Role.ToString();
            if (caller.Role is not (SupplyChainRole.Customer or SupplyChainRole.SupplyChainManager))
                throw new UnauthorizedAccessException("Caller is not authorized to place or cancel orders.");

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Mutation request must be a JSON object.");
            EnsureUniqueProperties(root);
            var mode = "execute";
            if (root.TryGetProperty("mode", out var modeNode))
            {
                if (modeNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(modeNode.GetString()))
                    throw new ArgumentException("'mode' must be a non-empty string.");
                mode = modeNode.GetString()!;
            }
            if (mode == "execute_approved")
            {
                if (root.EnumerateObject().Any(x => x.Name is not ("mode" or "approvalId")))
                    throw new ArgumentException("execute_approved accepts only mode and approvalId.");
                var handle = RequiredString(root, "approvalId");
                var approval = _approvals.Take(handle, subject, tenant, role);
                tool = approval.Tool;
                var currentFingerprint = Preview(approval.Tool, approval.Payload, subject, tenant);
                if (!StringComparer.Ordinal.Equals(currentFingerprint, approval.Fingerprint))
                    throw new InvalidOperationException("Approved mutation plan no longer matches the current request.");
                var receipt = Execute(approval.Tool, approval.Payload, subject, tenant, trace);
                Record("mutation.executed", subject, tenant, trace, $"{tool}; approved={approval.Fingerprint}");
                return new("executed", Receipt: receipt);
            }

            tool = RequiredString(root, "tool");
            if (tool is not ("place_order" or "cancel_order")) throw new ArgumentException("Unknown mutation tool.");
            var payload = CanonicalPayload(root, tool);
            ValidateShape(tool, payload);
            var fingerprint = Preview(tool, payload, subject, tenant);
            switch (mode)
            {
                case "dry_run":
                    Record("mutation.dry_run", subject, tenant, trace, $"{tool}; plan={fingerprint}");
                    return new("dry_run", PlanFingerprint: fingerprint);
                case "approve":
                    var handle = _approvals.Issue(subject, tenant, role, tool, payload, fingerprint);
                    Record("mutation.approved", subject, tenant, trace, $"{tool}; plan={fingerprint}");
                    return new("approved", ApprovalHandle: handle, PlanFingerprint: fingerprint);
                case "execute":
                    var receipt = Execute(tool, payload, subject, tenant, trace);
                    Record("mutation.executed", subject, tenant, trace, tool);
                    return new("executed", PlanFingerprint: fingerprint, Receipt: receipt);
                default: throw new ArgumentException("Mode must be dry_run, approve, execute or execute_approved.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            Record("mutation.denied", subject, tenant, trace, ex.Message);
            return new("denied", ErrorCode: "forbidden", ErrorMessage: ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record("mutation.rejected", subject, tenant, trace, ex.Message);
            return new("rejected", ErrorCode: ex is JsonException or ArgumentException ? "invalid_mutation" : "mutation_failed", ErrorMessage: ex.Message);
        }
    }

    private string Preview(string tool, string payload, string subject, string tenant) => tool switch
    {
        "place_order" => _place.Preview(subject, tenant, JsonSerializer.Deserialize<PlaceOrderCommand>(payload, JsonOptions)!),
        "cancel_order" => _cancel.Preview(subject, tenant, JsonSerializer.Deserialize<CancelOrderCommand>(payload, JsonOptions)!),
        _ => throw new ArgumentException("Unknown mutation tool.")
    };

    private MutationReceipt Execute(string tool, string payload, string subject, string tenant, string trace) => tool switch
    {
        "place_order" => _place.Place(subject, tenant, JsonSerializer.Deserialize<PlaceOrderCommand>(payload, JsonOptions)!, trace),
        "cancel_order" => _cancel.Cancel(subject, tenant, JsonSerializer.Deserialize<CancelOrderCommand>(payload, JsonOptions)!, trace),
        _ => throw new ArgumentException("Unknown mutation tool.")
    };

    private static string CanonicalPayload(JsonElement root, string tool)
    {
        EnsureUniqueProperties(root);
        var allowed = tool == "place_order"
            ? new HashSet<string>(["mode", "tool", "customerId", "lines", "idempotencyKey"], StringComparer.Ordinal)
            : new HashSet<string>(["mode", "tool", "orderId", "idempotencyKey"], StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw new ArgumentException($"Unknown mutation property '{property.Name}'.");
        if (tool == "place_order")
        {
            if (!root.TryGetProperty("lines", out var linesNode) || linesNode.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("'lines' must be an array.");
            var lines = new List<OrderLineCommand>();
            foreach (var line in linesNode.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each order line must be an object.");
                EnsureUniqueProperties(line);
                if (line.EnumerateObject().Any(x => x.Name is not ("productId" or "quantity")))
                    throw new ArgumentException("Order lines accept only productId and quantity.");
                if (!line.TryGetProperty("productId", out var product) || !product.TryGetInt32(out var productId) ||
                    !line.TryGetProperty("quantity", out var quantity) || !quantity.TryGetInt32(out var quantityValue))
                    throw new ArgumentException("Each order line requires integer productId and quantity values.");
                lines.Add(new OrderLineCommand(productId, quantityValue));
            }
            var command = new PlaceOrderCommand(RequiredInt(root, "customerId"),
                lines, RequiredString(root, "idempotencyKey"));
            return JsonSerializer.Serialize(command, JsonOptions);
        }
        return JsonSerializer.Serialize(new CancelOrderCommand(RequiredInt(root, "orderId"), RequiredString(root, "idempotencyKey")), JsonOptions);
    }

    private static void ValidateShape(string tool, string payload)
    {
        if (tool == "place_order")
        {
            var c = JsonSerializer.Deserialize<PlaceOrderCommand>(payload, JsonOptions)!;
            if (c.CustomerId <= 0 || c.Lines.Count == 0 || c.Lines.Any(x => x.ProductId <= 0 || x.Quantity <= 0))
                throw new ArgumentException("A customer and at least one positive product quantity are required.");
            if (string.IsNullOrWhiteSpace(c.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.");
        }
        else
        {
            var c = JsonSerializer.Deserialize<CancelOrderCommand>(payload, JsonOptions)!;
            if (c.OrderId <= 0 || string.IsNullOrWhiteSpace(c.IdempotencyKey)) throw new ArgumentException("Order id and idempotency key are required.");
        }
    }

    private static void EnsureUniqueProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Expected a JSON object.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new ArgumentException($"Duplicate mutation property '{property.Name}'.");
        }
    }

    private void Record(string eventName, string subject, string tenant, string trace, string detail) =>
        _audit.Enqueue(new(DateTimeOffset.UtcNow, eventName, subject, tenant, trace, detail));
    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException($"'{name}' is required.");
    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : throw new ArgumentException($"'{name}' must be an integer.");
    private static string NormalizeCorrelation(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            throw new ArgumentException("Invalid correlation id.");
        return value;
    }
}
