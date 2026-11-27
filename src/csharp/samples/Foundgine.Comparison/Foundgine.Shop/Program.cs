using System.Text;
using Foundgine.Core.Semantic;
using Foundgine.Core.Semantic.Authorization;
using Foundgine.Core.Semantic.Metadata;
using Foundgine.Core.Semantic.Security.Execution;
using Foundgine.Core.Semantic.Security.Warrants;
using Foundgine.Extensions.GraphQL.HotChocolate;
using Foundgine.Extensions.GraphQL.Http;
using Foundgine.Extensions.Keycloak.Configuration;
using Foundgine.Extensions.Keycloak.Hosting;
using Foundgine.Providers.EntityFrameworkCore;
using Foundgine.Providers.Tools.MCP;
using Foundgine.Runtime;
using Foundgine.Shop;
using Foundgine.Shop.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;

var builder = WebApplication.CreateBuilder(args);
var cs = builder.Configuration.GetConnectionString("Shop") ?? "Host=localhost;Port=5433;Database=shop;Username=shop;Password=shop";
var key = builder.Configuration["Jwt:SigningKey"] ?? "dev-only-signing-key-change-me-32bytes!!";

// ---- Authentication: plain JWT bearer (identical block in HotChocolate.Shop) ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = "https://auth.local", ValidAudience = "shop-api",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        NameClaimType = "sub", RoleClaimType = "role", ClockSkew = TimeSpan.FromSeconds(5)
    };
});
builder.Services.AddAuthorization();
var accessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(accessor);

// ---- Foundgine: model, EF provider, JWT -> warrant bridge, engine ----
var metadata = new ShopModel().Build();
var semanticModel = metadata.Discover();
builder.Services.AddFoundgineEf(metadata, (_, o) => o.UseNpgsql(cs), o => o.UseEngineProvider());

builder.Services.AddFoundgineKeycloakWarrants(o =>
{
    o.WarrantIssuer = "shop-jwt-bridge"; o.WarrantAudience = "foundgine";
    // Every role may read every entity; WHERE it may start and WHICH rows it sees is decided below.
    foreach (var role in new[] { "manager", "customer" })
        o.RoleGrants[role] = new[] { "Customer", "Order", "OrderLine", "Product" }
            .Select(e => new FoundgineKeycloakGrant { Capability = $"{e}.read", Operation = "read" }).ToList();
}).UseEphemeralWarrantKey(); // single instance only; use a mounted key / KMS provider when scaling out
builder.Services.AddSingleton<ISecurityExecutionContextProvider, JwtSecurityContextProvider>();
builder.Services.AddSingleton<ISecurityWarrantReplayStore, MemorySecurityWarrantReplayStore>();
builder.Services.AddSingleton<ISecurityWarrantRevocationStore, MemorySecurityWarrantRevocationStore>();

builder.Services.AddFoundgine(o =>
{
    o.Model = semanticModel;
    o.AuthorizationPolicy = new ShopAuthorizationPolicy(accessor); // Planner + SemanticAuthorizer are registered by AddFoundgine
    o.ExpectedWarrantIssuer = "shop-jwt-bridge";
});

// What a customer's row-level rule may reference: a validated claim, never protocol input. Shared by every protocol below.
ExecutionContext ShopContext()
{
    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
    var user = accessor.HttpContext?.User;
    if (user?.IsInRole("customer") == true && int.TryParse(user.FindFirst("customer_id")?.Value, out var id))
        values["user.CustomerId"] = id;
    return new ExecutionContext(values);
}

// ---- Protocol 1: GraphQL ----
builder.Services.AddSingleton(semanticModel);
builder.Services.AddScoped(_ => new HotChocolateSemanticAdapter(semanticModel));
builder.Services.AddScoped(sp => new FoundgineGraphQLQueryExecutor(
    sp.GetRequiredService<IFoundgine>(),
    sp.GetRequiredService<HotChocolateSemanticAdapter>(),
    sp.GetRequiredService<ISecurityExecutionContextProvider>(),
    ShopContext));

// ---- Protocol 2 (opt-in, Mcp:Enabled=true): MCP. Same model, same row-level policy, same JWT: no new mapping code. ----
var mcp = builder.Configuration.GetValue<bool>("Mcp:Enabled");
if (mcp)
{
    builder.Services.AddFoundgineMcp(ShopContext);
    builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<FoundgineMcpTools>();
}

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapFoundgineGraphQL(o =>
{
    // Customers hold OrderLine.read so their own orders can include lines, but only Customer and Order have
    // row-level predicates, so a customer must not START a query at the OrderLine root.
    o.QueryRootRoles["customer"] = ["manager", "customer"];
    o.QueryRootRoles["order"] = ["manager", "customer"];
    o.QueryRootRoles["product"] = ["manager", "customer"];
    o.QueryRootRoles["orderLine"] = ["manager"];

    // customer_id is a validated token claim, never a GraphQL argument.
    o.Authorize = ctx => ctx.User.IsInRole("customer") && !int.TryParse(ctx.User.FindFirst("customer_id")?.Value, out _)
        ? new GraphQLAdapterError("FORBIDDEN", "A customer_id claim is required for customer operations.", Category: "FORBIDDEN")
        : null;
});
if (mcp) app.MapMcp("/mcp").RequireAuthorization();
app.Run();
