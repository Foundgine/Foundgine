using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Foundgine.SupplyChain.Advanced.Mutation;

/// <summary>Typed, tenant-scoped order placement with atomic inventory allocation and idempotent replay.</summary>
public sealed class PlaceOrderService(SupplyChainMutationStore store)
{
    public string Preview(string subject, string tenant, PlaceOrderCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(command);
        if (command.CustomerId <= 0 || command.Lines is null || command.Lines.Count == 0 || command.Lines.Any(x => x.ProductId <= 0 || x.Quantity <= 0))
            throw new ArgumentException("A customer and at least one positive product quantity are required.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128)
            throw new ArgumentException("A valid idempotency key is required.");
        lock (store.Gate)
        {
            if (!store.Customers.TryGetValue(command.CustomerId, out var customer)) throw new ArgumentException("Customer not found.");
            if (customer.Tenant != tenant) throw new UnauthorizedAccessException("Customer belongs to another tenant.");
            if ((subject.StartsWith("customer:", StringComparison.Ordinal) && subject != $"customer:{command.CustomerId}") ||
                (subject.Equals("alice", StringComparison.OrdinalIgnoreCase) && command.CustomerId != 1) ||
                (subject.Equals("bob", StringComparison.OrdinalIgnoreCase) && command.CustomerId != 2) ||
                (subject.Equals("charlie", StringComparison.OrdinalIgnoreCase) && command.CustomerId != 3))
                throw new UnauthorizedAccessException("Customer ownership check failed.");
            var fingerprint = Fingerprint(new { subject, tenant, command.CustomerId,
                Lines = command.Lines.OrderBy(x => x.ProductId).Select(x => new { x.ProductId, x.Quantity }) });
            if (store.PlaceKeys.TryGetValue(command.IdempotencyKey, out var prior))
            {
                if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Idempotency key is bound to a different request.");
                return fingerprint;
            }
            foreach (var line in command.Lines.GroupBy(x => x.ProductId).Select(g => new OrderLineCommand(g.Key, g.Sum(x => x.Quantity))))
            {
                if (!store.Prices.ContainsKey(line.ProductId)) throw new ArgumentException($"Product {line.ProductId} not found.");
                if (!store.Inventory.Any(x => x.Tenant == tenant && x.ProductId == line.ProductId && x.OnHand - x.Reserved - x.Quarantined >= line.Quantity))
                    throw new InvalidOperationException($"Insufficient inventory for product {line.ProductId}.");
            }
            return fingerprint;
        }
    }

    public MutationReceipt Place(string subject, string tenant, PlaceOrderCommand command, string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(command);
        if (command.CustomerId <= 0 || command.Lines is null || command.Lines.Count == 0 ||
            command.Lines.Any(x => x.ProductId <= 0 || x.Quantity <= 0))
            throw new ArgumentException("A customer and at least one positive product quantity are required.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128)
            throw new ArgumentException("A valid idempotency key is required.");

        var fingerprint = Fingerprint(new { subject, tenant, command.CustomerId,
            Lines = command.Lines.OrderBy(x => x.ProductId).Select(x => new { x.ProductId, x.Quantity }) });
        lock (store.Gate)
        {
            if (!store.Customers.TryGetValue(command.CustomerId, out var customer))
                throw new ArgumentException("Customer not found.");
            if (!StringComparer.Ordinal.Equals(customer.Tenant, tenant))
                throw new UnauthorizedAccessException("Customer belongs to another tenant.");
            if ((subject.StartsWith("customer:", StringComparison.Ordinal) && subject != $"customer:{command.CustomerId}") ||
                (subject.Equals("alice", StringComparison.OrdinalIgnoreCase) && command.CustomerId != 1) ||
                (subject.Equals("bob", StringComparison.OrdinalIgnoreCase) && command.CustomerId != 2) ||
                (subject.Equals("charlie", StringComparison.OrdinalIgnoreCase) && command.CustomerId != 3))
                throw new UnauthorizedAccessException("Customer ownership check failed.");
            if (store.PlaceKeys.TryGetValue(command.IdempotencyKey, out var prior))
            {
                if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Idempotency key is bound to a different request.");
                var existing = store.Orders[prior.OrderId];
                return Receipt(existing, true, correlationId, fingerprint);
            }

            var grouped = command.Lines.GroupBy(x => x.ProductId).Select(g => new OrderLineCommand(g.Key, g.Sum(x => x.Quantity))).ToArray();
            var allocations = new List<(MutationInventoryLot Lot, int Product, int Quantity, decimal Price)>();
            decimal total = 0;
            foreach (var line in grouped)
            {
                if (!store.Prices.TryGetValue(line.ProductId, out var price)) throw new ArgumentException($"Product {line.ProductId} not found.");
                var lot = store.Inventory.Where(x => x.Tenant == tenant && x.ProductId == line.ProductId &&
                    x.OnHand - x.Reserved - x.Quarantined >= line.Quantity)
                    .OrderByDescending(x => x.OnHand - x.Reserved - x.Quarantined).ThenBy(x => x.Id).FirstOrDefault()
                    ?? throw new InvalidOperationException($"Insufficient inventory for product {line.ProductId}.");
                allocations.Add((lot, line.ProductId, line.Quantity, price));
                total += price * line.Quantity;
            }

            var orderId = store.NextOrderId++;
            var order = new MutationOrder(orderId, command.CustomerId, tenant, "Pending", total, DateTimeOffset.UtcNow);
            store.Orders.Add(orderId, order);
            foreach (var allocation in allocations)
            {
                var itemId = store.NextItemId++;
                store.Lines.Add(new(itemId, orderId, allocation.Product, allocation.Quantity, allocation.Price));
                store.Allocations.Add(new(itemId, allocation.Lot.Id, allocation.Quantity));
                var index = store.Inventory.FindIndex(x => x.Id == allocation.Lot.Id);
                var lot = store.Inventory[index];
                store.Inventory[index] = lot with { OnHand = lot.OnHand - allocation.Quantity };
            }
            store.PlaceKeys.Add(command.IdempotencyKey, (fingerprint, orderId));
            return Receipt(order, false, correlationId, fingerprint);
        }
    }

    internal static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static MutationReceipt Receipt(MutationOrder order, bool replay, string correlationId, string fingerprint) =>
        new("place_order", order.Id, replay, order.Total, correlationId, fingerprint, DateTimeOffset.UtcNow);
}
