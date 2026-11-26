using System.Text.Json;
using HotChocolate;
using HotChocolate.Types;
using Foundgine.SupplyChain.Advanced.OpenIntent.Services;

namespace Foundgine.SupplyChain.Advanced.OpenIntent.Api;

// SupplyChainService intentionally returns provider-neutral object payloads. Serialize them to JsonElement
// before exposing Hot Chocolate's Any scalar; do not leak anonymous CLR types or EF entities into the schema.
internal static class GraphQlResult
{
    public static JsonElement From(object value) => JsonSerializer.SerializeToElement(value);
    public static async Task<JsonElement> From(Task<object> value) => JsonSerializer.SerializeToElement(await value.ConfigureAwait(false));
}

public sealed class SupplyChainGraphQLQuery(IHttpContextAccessor httpContextAccessor)
{
    [GraphQLType(typeof(AnyType))] public JsonElement Capabilities([Service] SupplyChainService service) => GraphQlResult.From(service.DescribeCapabilities(Actor()));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> Products([Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.ListProducts(Actor(), ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> Product(int productId, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.GetProduct(Actor(), productId, ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> Inventory(int productId, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.GetInventory(Actor(), productId, ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> Customers([Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.ListCustomers(Actor(), ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> Orders(int customerId, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.GetMyOrders(Actor(), customerId, ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> Order(int customerId, int orderId, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.GetOrder(Actor(), customerId, orderId, ct));

    private string Actor() => OpenIntentDemoSecurity.TryGetAuthenticatedActor(httpContextAccessor.HttpContext!)
        ?? throw new GraphQLException(ErrorBuilder.New().SetMessage("Request blocked.").SetCode("REQUEST_BLOCKED").Build());
}

public sealed class SupplyChainGraphQLMutation(IHttpContextAccessor httpContextAccessor)
{
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> PlaceOrder(int customerId, OrderLine[] lines, string idempotencyKey, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.PlaceOrder(Actor(), customerId, lines, idempotencyKey, ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> CancelOrder(int customerId, int orderId, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.CancelOrder(Actor(), customerId, orderId, ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> UpdateInventory(int warehouseId, int productId, int quantity, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.UpdateInventory(Actor(), warehouseId, productId, quantity, ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> CreateShipment(int orderId, int carrierId, int warehouseId, string trackingNumber, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.CreateShipment(Actor(), orderId, carrierId, warehouseId, trackingNumber, ct));
    [GraphQLType(typeof(AnyType))] public Task<JsonElement> UpdateShipment(int shipmentId, string status, [Service] SupplyChainService service, CancellationToken ct) => GraphQlResult.From(service.UpdateShipment(Actor(), shipmentId, status, ct));

    private string Actor() => OpenIntentDemoSecurity.TryGetAuthenticatedActor(httpContextAccessor.HttpContext!)
        ?? throw new GraphQLException(ErrorBuilder.New().SetMessage("Request blocked.").SetCode("REQUEST_BLOCKED").Build());
}
