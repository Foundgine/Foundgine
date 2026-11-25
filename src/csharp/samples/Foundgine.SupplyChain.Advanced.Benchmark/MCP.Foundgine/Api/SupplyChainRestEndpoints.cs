using System.Text.Json;
using Foundgine.SupplyChain.Advanced.OpenIntent.Services;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;

namespace Foundgine.SupplyChain.Advanced.OpenIntent.Api;

/// <summary>REST is an adapter only. It derives the actor from validated host credentials and delegates every
/// operation to SupplyChainService; it contains no business rules or independent authorization decisions.</summary>
public static class SupplyChainRestEndpoints
{
    public static IEndpointRouteBuilder MapSupplyChainRest(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/supply-chain").WithTags("Supply chain");
        api.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (McpException ex) { return Results.Problem(title: "request_blocked", detail: ex.Message, statusCode: 403); }
            catch (ArgumentException ex) { return Results.Problem(title: "invalid_request", detail: ex.Message, statusCode: 400); }
        });

        api.MapGet("/capabilities", (HttpContext http, SupplyChainService service) =>
            service.DescribeCapabilities(Actor(http)));
        api.MapGet("/products", (HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.ListProducts(Actor(http), ct));
        api.MapGet("/products/{productId:int}", (int productId, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.GetProduct(Actor(http), productId, ct));
        api.MapGet("/inventory/{productId:int}", (int productId, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.GetInventory(Actor(http), productId, ct));
        api.MapGet("/customers", (HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.ListCustomers(Actor(http), ct));
        api.MapGet("/suppliers/top-overdue-orders", (string state, string? supplierName, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.FindTopSupplierOverdueOrders(Actor(http), state, supplierName, ct));
        api.MapGet("/suppliers", (HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.ListSuppliers(Actor(http), ct));
        api.MapGet("/customers/{customerId:int}/orders", (int customerId, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.GetMyOrders(Actor(http), customerId, ct));
        api.MapGet("/customers/{customerId:int}/orders/{orderId:int}", (int customerId, int orderId, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.GetOrder(Actor(http), customerId, orderId, ct));
        api.MapPost("/orders", async ([FromBody] PlaceOrderRequest request, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            Results.Ok(await service.PlaceOrder(Actor(http), request.CustomerId, request.Lines, request.IdempotencyKey, ct)));
        api.MapPost("/orders/{orderId:int}/cancel", (int orderId, [FromBody] CancelOrderRequest request, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.CancelOrder(Actor(http), request.CustomerId, orderId, ct));
        api.MapGet("/shipments/{shipmentId:int}", (int shipmentId, [FromQuery] int customerId, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.GetShipment(Actor(http), customerId, shipmentId, ct));
        api.MapPost("/inventory/adjustments", (InventoryAdjustmentRequest request, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.UpdateInventory(Actor(http), request.WarehouseId, request.ProductId, request.Quantity, ct));
        api.MapPost("/shipments", (CreateShipmentRequest request, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.CreateShipment(Actor(http), request.OrderId, request.CarrierId, request.WarehouseId, request.TrackingNumber, ct));
        api.MapGet("/entities", (HttpContext http, SupplyChainService service) => service.ListEntities(Actor(http)));
        api.MapPost("/entities/{entity}/query", (string entity, ReadEntityRequest request, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.ReadEntity(Actor(http), entity, request.EqualsValues, request.Fields, ct));
        api.MapGet("/entities/{entity}/{id}", (string entity, int id, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.GetEntity(Actor(http), entity, JsonDocument.Parse(JsonSerializer.Serialize(id)).RootElement.Clone(), ct));
        api.MapPost("/entities/{entity}", (string entity, JsonElement body, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.CreateEntity(Actor(http), entity, body, ct));
        api.MapPut("/entities/{entity}/{id}", (string entity, int id, JsonElement values, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.UpdateEntity(Actor(http), entity, JsonDocument.Parse(JsonSerializer.Serialize(id)).RootElement.Clone(), values, ct));
        api.MapDelete("/entities/{entity}/{id}", (string entity, int id, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.DeleteEntity(Actor(http), entity, JsonDocument.Parse(JsonSerializer.Serialize(id)).RootElement.Clone(), ct));
        api.MapPut("/shipments/{shipmentId:int}/status", (int shipmentId, UpdateShipmentRequest request, HttpContext http, SupplyChainService service, CancellationToken ct) =>
            service.UpdateShipment(Actor(http), shipmentId, request.Status, ct));
        return endpoints;
    }

    private static string Actor(HttpContext context) =>
        OpenIntentDemoSecurity.TryGetAuthenticatedActor(context)
        ?? throw new McpException("Request blocked: valid demo actor credentials are required.");

    public sealed record ReadEntityRequest(JsonElement? EqualsValues, string[]? Fields);
    public sealed record PlaceOrderRequest(int CustomerId, OrderLine[] Lines, string IdempotencyKey);
    public sealed record CancelOrderRequest(int CustomerId);
    public sealed record InventoryAdjustmentRequest(int WarehouseId, int ProductId, int Quantity);
    public sealed record CreateShipmentRequest(int OrderId, int CarrierId, int WarehouseId, string TrackingNumber);
    public sealed record UpdateShipmentRequest(string Status);
}
