using Wafish.Dashboard.Infrastructure.Plugin;
using System.Security.Claims;
using Wafish.Plugin.ExternalLogin;
using Wafish.Runtime;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Wafish.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var role = builder.Configuration["Sample:Role"] ?? "gateway";
if (role == "api")
{
    builder.Services.AddAuthentication("Bearer").AddJwtBearer("Bearer", options =>
    {
        options.Authority = builder.Configuration["Sample:Authority"];
        options.Audience = "support-api";
        options.MapInboundClaims = false;
    });
    builder.Services.AddAuthorization(options => options.AddPolicy("support", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => context.User.FindAll("scope").SelectMany(c => c.Value.Split(' ')).Contains("support:write"))));
}
else if (role == "gateway")
{
    builder.Services.AddWafishStorage().AddWafishRuntime();
    builder.Services.AddWafishExternalLogin(builder.Configuration, builder.Environment);
    builder.Services.AddAuthorization(options => options.AddPolicy("visitor-initializing", policy => policy.RequireAssertion(_ => false)));
}
var app = builder.Build();
var ready = role != "gateway";
app.Use(async (context, next) => { if (!ready) context.Response.StatusCode = 503; else await next(context); });
app.MapGet("/ready", () => Results.Ok());
if (role == "static")
{
    app.UseDefaultFiles(); app.UseStaticFiles();
}
else if (role == "api")
{
    app.UseAuthentication(); app.UseAuthorization();
    app.MapPost("/api/support/tickets", (ClaimsPrincipal user) => Results.Json(new { id = Guid.NewGuid(), submittedBy = user.FindFirst("sub")?.Value }, statusCode: 201)).RequireAuthorization("support");
}
else if (role == "gateway") app.UseWafishGateway();
else throw new InvalidOperationException("Unknown external login sample role.");
await app.StartAsync();
if (role == "gateway")
{
    var config = app.Services.GetRequiredService<IDynamicYarpConfigService>();
    foreach (var (id, address) in new[] { ("static", builder.Configuration["Sample:StaticOrigin"]!), ("support", builder.Configuration["Sample:ApiOrigin"]!) })
    {
        var result = await config.TryAddClusterConfig(new() { ClusterId = id, Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig> { [id] = new() { Address = address } } });
        if (!result.Success) throw new InvalidOperationException("Could not seed the external login sample cluster.");
    }
    var authority = new Uri(builder.Configuration["Gateway:ExternalLogin:Sites:0:Origin"]!).Authority;
    foreach (var (id, path, cluster, mode, order) in new[] { ("private", "/logged-in/{**catch-all}", "static", "page", 0), ("support", "/api/support/{**catch-all}", "support", "api", 0), ("public", "/{**catch-all}", "static", "", 100) })
    {
        var route = new Yarp.ReverseProxy.Configuration.RouteConfig { RouteId = id, ClusterId = cluster, Order = order, AuthorizationPolicy = mode.Length == 0 ? null : "visitor-initializing", Match = new() { Hosts = [authority], Path = path } };
        var result = await config.TryAddRouteConfig(route);
        if (!result.Success) throw new InvalidOperationException("Could not seed the external login sample route.");
        if (mode.Length == 0) continue;
        var stored = (await app.Services.GetRequiredService<IRouteRepository>().GetAllRoutesAsync()).Single(r => r.RouteId == id);
        var existing = (await app.Services.GetRequiredService<IPluginConfigurationRepository>().GetBindingsAsync()).FirstOrDefault(b => b.PluginId == "external-login" && b.RouteUid == stored.RouteUid);
        await app.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(new()
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            PluginId = "external-login", PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = id, RouteUid = stored.RouteUid,
            ConfigJson = System.Text.Json.JsonSerializer.Serialize(new { site = "website", mode, apiTarget = mode == "api" ? "support" : null })
        });
        if (!(await config.TryAddRouteConfig(route with { AuthorizationPolicy = null })).Success)
            throw new InvalidOperationException("Could not activate the protected sample route.");
    }
}
ready = true;
await app.WaitForShutdownAsync();
