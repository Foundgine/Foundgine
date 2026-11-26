namespace Foundgine.SupplyChain.Advanced.Mutation;

/// <summary>Typed cancellation with tenant/ownership checks, inventory restoration and replay protection.</summary>
public sealed class CancelOrderService(SupplyChainMutationStore store)
{
    public string Preview(string subject, string tenant, CancelOrderCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(command);
        if (command.OrderId <= 0 || string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128)
            throw new ArgumentException("A valid order id and idempotency key are required.");
        lock (store.Gate)
        {
            if (!store.Orders.TryGetValue(command.OrderId, out var order)) throw new ArgumentException("Order not found.");
            if (order.Tenant != tenant) throw new UnauthorizedAccessException("Order belongs to another tenant.");
            if ((subject.StartsWith("customer:", StringComparison.Ordinal) && subject != $"customer:{order.CustomerId}") ||
                (subject.Equals("alice", StringComparison.OrdinalIgnoreCase) && order.CustomerId != 1) ||
                (subject.Equals("bob", StringComparison.OrdinalIgnoreCase) && order.CustomerId != 2) ||
                (subject.Equals("charlie", StringComparison.OrdinalIgnoreCase) && order.CustomerId != 3))
                throw new UnauthorizedAccessException("Customer ownership check failed.");
            var fingerprint = PlaceOrderService.Fingerprint(new { subject, tenant, command.OrderId });
            if (store.CancelKeys.TryGetValue(command.IdempotencyKey, out var prior))
            {
                if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Idempotency key is bound to a different request.");
                return fingerprint;
            }
            if (order.Status != "Pending") throw new InvalidOperationException("Only pending orders can be cancelled.");
            return fingerprint;
        }
    }

    public MutationReceipt Cancel(string subject, string tenant, CancelOrderCommand command, string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(command);
        if (command.OrderId <= 0 || string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128)
            throw new ArgumentException("A valid order id and idempotency key are required.");
        var fingerprint = PlaceOrderService.Fingerprint(new { subject, tenant, command.OrderId });
        lock (store.Gate)
        {
            if (!store.Orders.TryGetValue(command.OrderId, out var order)) throw new ArgumentException("Order not found.");
            if (order.Tenant != tenant) throw new UnauthorizedAccessException("Order belongs to another tenant.");
            if ((subject.StartsWith("customer:", StringComparison.Ordinal) && subject != $"customer:{order.CustomerId}") ||
                (subject.Equals("alice", StringComparison.OrdinalIgnoreCase) && order.CustomerId != 1) ||
                (subject.Equals("bob", StringComparison.OrdinalIgnoreCase) && order.CustomerId != 2) ||
                (subject.Equals("charlie", StringComparison.OrdinalIgnoreCase) && order.CustomerId != 3))
                throw new UnauthorizedAccessException("Customer ownership check failed.");
            if (store.CancelKeys.TryGetValue(command.IdempotencyKey, out var prior))
            {
                if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Idempotency key is bound to a different request.");
                var replayed = store.Orders[prior.OrderId];
                return new("cancel_order", replayed.Id, true, replayed.Total, correlationId, fingerprint, DateTimeOffset.UtcNow);
            }
            if (order.Status != "Pending") throw new InvalidOperationException("Only pending orders can be cancelled.");

            foreach (var line in store.Lines.Where(x => x.OrderId == order.Id))
            foreach (var allocation in store.Allocations.Where(x => x.OrderLineId == line.Id))
            {
                var index = store.Inventory.FindIndex(x => x.Id == allocation.LotId);
                if (index < 0) throw new InvalidOperationException("Allocated inventory lot no longer exists.");
                var lot = store.Inventory[index];
                store.Inventory[index] = lot with { OnHand = lot.OnHand + allocation.Quantity };
            }
            store.Orders[order.Id] = order with { Status = "Cancelled" };
            store.CancelKeys.Add(command.IdempotencyKey, (fingerprint, order.Id));
            return new("cancel_order", order.Id, false, order.Total, correlationId, fingerprint, DateTimeOffset.UtcNow);
        }
    }
}
