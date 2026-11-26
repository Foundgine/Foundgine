namespace Foundgine.SupplyChain.Advanced.Mutation;

/// <summary>Typed application commands. Identity and tenant are deliberately absent from command payloads.</summary>
public sealed record PlaceOrderCommand(int CustomerId, IReadOnlyList<OrderLineCommand> Lines, string IdempotencyKey);
public sealed record OrderLineCommand(int ProductId, int Quantity);
public sealed record CancelOrderCommand(int OrderId, string IdempotencyKey);
public sealed record MutationReceipt(string Operation, int OrderId, bool Replay, decimal Total,
    string CorrelationId, string PlanFingerprint, DateTimeOffset CompletedAt);
public sealed record MutationAuditEntry(DateTimeOffset At, string Event, string Subject, string Tenant,
    string CorrelationId, string Detail);
public sealed record MutationDispatchResult(string Status, string? ApprovalHandle = null,
    string? PlanFingerprint = null, MutationReceipt? Receipt = null, string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Small, explicit in-memory fixture used only by the Advanced mutation walkthrough.</summary>
public sealed class SupplyChainMutationStore
{
    internal readonly object Gate = new();
    internal readonly Dictionary<int, (string Tenant, string Name)> Customers = new()
        { [1] = ("tenant-a", "Alice"), [2] = ("tenant-a", "Bob"), [3] = ("tenant-b", "Charlie") };
    internal readonly Dictionary<int, decimal> Prices = new()
        { [1] = 12.25m, [2] = 19.50m, [3] = 26.75m, [4] = 34.00m, [5] = 41.25m, [6] = 48.50m };
    internal readonly List<MutationInventoryLot> Inventory =
    [
        new(900, "tenant-a", 1, 4, 70m, 20m, 10m), new(901, "tenant-a", 1, 5, 90m, 75m, 0m),
        new(902, "tenant-a", 1, 3, 25m, 10m, 5m), new(903, "tenant-b", 3, 4, 5000m, 0m, 0m)
    ];
    internal readonly Dictionary<int, MutationOrder> Orders = new();
    internal readonly Dictionary<string, (string Fingerprint, int OrderId)> PlaceKeys = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, (string Fingerprint, int OrderId)> CancelKeys = new(StringComparer.Ordinal);
    internal int NextOrderId = 1;
    internal int NextItemId = 1;
    internal readonly List<MutationOrderLine> Lines = [];
    internal readonly List<MutationAllocation> Allocations = [];
}

internal sealed record MutationInventoryLot(int Id, string Tenant, int WarehouseId, int ProductId, decimal OnHand,
    decimal Reserved, decimal Quarantined);
internal sealed record MutationOrder(int Id, int CustomerId, string Tenant, string Status, decimal Total,
    DateTimeOffset CreatedAt);
internal sealed record MutationOrderLine(int Id, int OrderId, int ProductId, int Quantity, decimal UnitPrice);
internal sealed record MutationAllocation(int OrderLineId, int LotId, int Quantity);
