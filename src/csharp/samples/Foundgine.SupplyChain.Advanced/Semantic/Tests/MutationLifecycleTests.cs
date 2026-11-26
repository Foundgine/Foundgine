using System.Security.Claims;
using System.Text.Json;
using Foundgine.SupplyChain.Advanced.Intent;
using Foundgine.SupplyChain.Advanced.Mutation;
using Xunit;

namespace Foundgine.SupplyChain.Advanced.Tests;

public sealed class MutationLifecycleTests
{
    private static ClaimsPrincipal Caller(string tenant = "tenant-a", string role = "Customer", string subject = "alice") =>
        new(new ClaimsIdentity([
            new Claim("sub", subject), new Claim(IntentCaller.TenantClaim, tenant),
            new Claim(ClaimTypes.Role, role)
        ], "mutation-test"));

    [Fact]
    public async Task Dry_run_and_approval_do_not_execute_until_single_use_handle_is_consumed()
    {
        var dispatcher = new SupplyChainMutationDispatcher();
        const string request = """{"tool":"place_order","customerId":1,"lines":[{"productId":4,"quantity":2}],"idempotencyKey":"place-001"}""";
        var dryRun = await dispatcher.DispatchJsonAsync(AddMode(request, "dry_run"), Caller(), "trace-dry");
        Assert.Equal("dry_run", dryRun.Status);
        Assert.NotNull(dryRun.PlanFingerprint);

        var approved = await dispatcher.DispatchJsonAsync(AddMode(request, "approve"), Caller(), "trace-approve");
        Assert.Equal("approved", approved.Status);
        Assert.False(string.IsNullOrWhiteSpace(approved.ApprovalHandle));

        var crossTenant = await dispatcher.DispatchJsonAsync(
            $$"""{"mode":"execute_approved","approvalId":"{{approved.ApprovalHandle}}"}""", Caller("tenant-b", subject: "alice"), "trace-denied");
        Assert.Equal("denied", crossTenant.Status);

        var executed = await dispatcher.DispatchJsonAsync(
            $$"""{"mode":"execute_approved","approvalId":"{{approved.ApprovalHandle}}"}""", Caller(), "trace-execute");
        Assert.Equal("executed", executed.Status);
        Assert.Equal("trace-execute", executed.Receipt!.CorrelationId);
        Assert.Equal(68.00m, executed.Receipt.Total);

        var replay = await dispatcher.DispatchJsonAsync(
            $$"""{"mode":"execute_approved","approvalId":"{{approved.ApprovalHandle}}"}""", Caller(), "trace-replay");
        Assert.Equal("rejected", replay.Status);
        Assert.Contains(dispatcher.Audit, x => x.CorrelationId == "trace-denied" && x.Event == "mutation.denied");
    }

    [Fact]
    public async Task Place_and_cancel_are_tenant_scoped_and_idempotent()
    {
        var dispatcher = new SupplyChainMutationDispatcher();
        const string place = """{"tool":"place_order","customerId":1,"lines":[{"productId":4,"quantity":2}],"idempotencyKey":"place-002"}""";
        var created = await dispatcher.DispatchJsonAsync(place, Caller(), "place-trace");
        Assert.Equal("executed", created.Status);
        Assert.Equal(68.00m, created.Receipt!.Total);

        var same = await dispatcher.DispatchJsonAsync(place, Caller(), "place-replay-trace");
        Assert.True(same.Receipt!.Replay);
        Assert.Equal(created.Receipt.OrderId, same.Receipt.OrderId);

        var cancel = $$"""{"tool":"cancel_order","orderId":{{created.Receipt.OrderId}},"idempotencyKey":"cancel-002"}""";
        var cancelled = await dispatcher.DispatchJsonAsync(cancel, Caller(), "cancel-trace");
        Assert.Equal("executed", cancelled.Status);
        var replay = await dispatcher.DispatchJsonAsync(cancel, Caller(), "cancel-replay");
        Assert.True(replay.Receipt!.Replay);

        var crossTenant = await dispatcher.DispatchJsonAsync(cancel, Caller("tenant-b", subject: "charlie"), "cross-tenant");
        Assert.Equal("denied", crossTenant.Status);
    }

    [Fact]
    public async Task Dispatcher_rejects_identity_fields_and_invalid_correlation_ids()
    {
        var dispatcher = new SupplyChainMutationDispatcher();
        var spoof = await dispatcher.DispatchJsonAsync(
            """{"tool":"place_order","tenant":"tenant-b","customerId":1,"lines":[{"productId":4,"quantity":1}],"idempotencyKey":"x"}""",
            Caller(), "trace-spoof");
        Assert.Equal("rejected", spoof.Status);
        Assert.Equal("invalid_mutation", spoof.ErrorCode);

        var invalidTrace = await dispatcher.DispatchJsonAsync(
            """{"tool":"place_order","customerId":1,"lines":[{"productId":4,"quantity":1}],"idempotencyKey":"y"}""",
            Caller(), "bad trace");
        Assert.Equal("rejected", invalidTrace.Status);
    }


    [Fact]
    public async Task Tenant_b_customer_can_place_and_cancel_own_order_without_cross_customer_bypass()
    {
        var dispatcher = new SupplyChainMutationDispatcher();
        var charlie = Caller("tenant-b", subject: "charlie");
        const string place = """{"tool":"place_order","customerId":3,"lines":[{"productId":4,"quantity":1}],"idempotencyKey":"charlie-place"}""";
        var created = await dispatcher.DispatchJsonAsync(place, charlie, "charlie-place-trace");
        Assert.Equal("executed", created.Status);

        var cancel = $$"""{"tool":"cancel_order","orderId":{{created.Receipt!.OrderId}},"idempotencyKey":"charlie-cancel"}""";
        var cancelled = await dispatcher.DispatchJsonAsync(cancel, charlie, "charlie-cancel-trace");
        Assert.Equal("executed", cancelled.Status);
        Assert.Equal("charlie-cancel-trace", cancelled.Receipt!.CorrelationId);
    }

    [Fact]
    public async Task Duplicate_and_nested_unknown_properties_are_rejected()
    {
        var dispatcher = new SupplyChainMutationDispatcher();
        var duplicate = await dispatcher.DispatchJsonAsync(
            """{"tool":"place_order","customerId":1,"customerId":2,"lines":[{"productId":4,"quantity":1}],"idempotencyKey":"duplicate"}""",
            Caller(), "duplicate-property");
        Assert.Equal("rejected", duplicate.Status);
        Assert.Equal("invalid_mutation", duplicate.ErrorCode);

        var nested = await dispatcher.DispatchJsonAsync(
            """{"tool":"place_order","customerId":1,"lines":[{"productId":4,"quantity":1,"tenant":"tenant-b"}],"idempotencyKey":"nested"}""",
            Caller(), "nested-property");
        Assert.Equal("rejected", nested.Status);
        Assert.Equal("invalid_mutation", nested.ErrorCode);
    }

    private static string AddMode(string json, string mode)
    {
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("mode", mode);
            foreach (var (key, value) in values) { writer.WritePropertyName(key); value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
