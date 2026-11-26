using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Models;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugin.RateLimit.Redis;
using Wafish.Plugins;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static partial class RedisFailureChecks
{
    private static readonly RedisRateLimitExecutionConfig TenantConfig = new()
    {
        PartitionKey = "Tenant", TenantClaimType = "tenant", TenantClaimIssuer = "trusted-issuer",
        AllowedTenants = ["alpha", "beta"], Limit = 2, WindowSeconds = 30, FailureMode = "FailOpen"
    };
    private static ClaimsPrincipal Tenant(string value, string issuer = "trusted-issuer", bool authenticated = true)
        => new(new ClaimsIdentity([new Claim("tenant", value, ClaimValueTypes.String, issuer)], authenticated ? "test-authentication" : null));

    private static async Task TenantPartitionsAsync()
    {
        var schema = new RedisRateLimitPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
        var validator = new PluginConfigurationSchemaValidator();
        foreach (var settings in new object[] {
            new { enabled = true }, new { partitionKey = "Tenant", tenantClaimType = "tenant", tenantClaimIssuer = "trusted", allowedTenants = new[] { "a" } } })
            Check(validator.TryValidate(JsonSerializer.Serialize(settings), schema, out _, out _), "Valid tenant policy rejected.");
        foreach (var settings in new object[] {
            new { partitionKey = "Tenant" }, new { partitionKey = "Tenant", tenantClaimType = "", tenantClaimIssuer = "trusted", allowedTenants = new[] { "a" } },
            new { partitionKey = "Tenant", tenantClaimType = "tenant", tenantClaimIssuer = "", allowedTenants = new[] { "a" } },
            new { partitionKey = "Tenant", tenantClaimType = "tenant", tenantClaimIssuer = "trusted", allowedTenants = Array.Empty<string>() },
            new { allowedTenants = new[] { "a", "a" } }, new { allowedTenants = Enumerable.Range(0,257).Select(i => i.ToString()).ToArray() } })
            Check(!validator.TryValidate(JsonSerializer.Serialize(settings), schema, out _, out _), "Invalid tenant policy accepted.");
        var store = new TenantStore();
        foreach (var (user, status) in new[] {
            (new ClaimsPrincipal(), 401), (Tenant("alpha", authenticated: false), 401),
            (Tenant("alpha", "untrusted"), 403), (Tenant("missing"), 403),
            (new ClaimsPrincipal(new ClaimsIdentity([], "authenticated")), 403) })
        {
            var rejected = await InvokeTenant(store, TenantConfig, user);
            Check(rejected.Response.StatusCode == status && rejected.Response.Headers["X-RateLimit-Status"] == "tenant-rejected", "Missing or untrusted tenant bypassed rejection.");
        }
        var ambiguous = Tenant("alpha"); ((ClaimsIdentity)ambiguous.Identity!).AddClaim(new Claim("tenant", "beta", ClaimValueTypes.String, "trusted-issuer"));
        Check((await InvokeTenant(store, TenantConfig, ambiguous)).Response.StatusCode == 403 && store.Calls == 0, "Rejected tenants reached Redis.");
        Check((await InvokeTenant(store, TenantConfig, Tenant("alpha"))).Response.StatusCode == 200, "Allowed tenant rejected.");
        Check((await InvokeTenant(store, TenantConfig, Tenant("alpha"), "192.0.2.2")).Response.StatusCode == 200, "Tenant partition changed with client address.");
        var exhausted = await InvokeTenant(store, TenantConfig, Tenant("alpha"));
        Check(exhausted.Response.StatusCode == 429 && exhausted.Response.Headers.RetryAfter == "1", "Shared tenant allowance was not enforced.");
        Check((await InvokeTenant(store, TenantConfig, Tenant("beta"))).Response.StatusCode == 200 && store.Keys.Count == 2, "Tenants shared a quota unexpectedly.");
        Check(store.Keys.All(key => !key.Contains("alpha") && !key.Contains("beta") && key.Contains(":tenant:")), "Raw tenant values entered quota keys.");
        var before = store.Calls;
        for (var index = 0; index < 100; index++) await InvokeTenant(store, TenantConfig, Tenant("unlisted-" + index));
        Check(store.Calls == before && store.Keys.Count == 2, "Unlisted tenant churn created keys.");
        Check((await InvokeTenant(store, TenantConfig with { AllowedTenants = null! }, Tenant("alpha"))).Response.StatusCode == 403, "Null allowlist bypassed rejection.");
        Check((await InvokeTenant(store, TenantConfig with { PartitionKey = "invalid" }, Tenant("alpha"))).Response.StatusCode == 503, "Invalid raw partition enabled a fallback.");
        var ip = new TenantStore();
        await InvokeTenant(ip, TenantConfig with { PartitionKey = "IpAddress" }, new ClaimsPrincipal());
        Check(ip.Keys.Single().EndsWith(":127.0.0.1", StringComparison.Ordinal), "Default IP partition changed.");
    }

    private static async Task TenantLiveAsync(RedisLuaRateLimitStore first, RedisLuaRateLimitStore second)
    {
        var config = TenantConfig with { KeyPrefix = "tenant-live:" + Guid.NewGuid().ToString("N"), FailureMode = "FailClosed", Algorithm = "TokenBucket", WindowSeconds = 3600 };
        Check((await InvokeTenant(first, config, Tenant("alpha"))).Response.StatusCode == 200, "Live tenant first request failed.");
        Check((await InvokeTenant(second, config, Tenant("alpha"), "192.0.2.2")).Response.StatusCode == 200, "Replicated tenant allowance failed.");
        Check((await InvokeTenant(first, config, Tenant("alpha"))).Response.StatusCode == 429, "Replicas did not share tenant quota.");
        Check((await InvokeTenant(second, config, Tenant("beta"))).Response.StatusCode == 200, "Live tenant isolation failed.");
    }

    private static async Task<HttpContext> InvokeTenant(IDistributedRateLimitStore store, RedisRateLimitExecutionConfig config, ClaimsPrincipal user, string address = "127.0.0.1")
    {
        var route = new RouteConfig { RouteId = "tenant-route", Match = new() { Path = "/tenant" } };
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                [new("tenant-binding", "rate-limit-redis", PluginBindingScope.Route, route.RouteId, JsonSerializer.Serialize(config), 1, 1, 0)]) });
        var plans = new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
        var middleware = new RedisRateLimitMiddleware(context => { context.Items["forwarded"] = true; return Task.CompletedTask; },
            NullLogger<RedisRateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store, plans);
        var context = new DefaultHttpContext { User = user };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address); context.Request.Headers["X-Tenant"] = "alpha";
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        using var output = new MemoryStream(); context.Response.Body = output;
        await middleware.InvokeAsync(context);
        Check(context.Items.ContainsKey("forwarded") == (context.Response.StatusCode == 200), "Rejected tenant reached upstream.");
        return context;
    }
    private sealed class TenantStore : IDistributedRateLimitStore
    {
        private readonly ConcurrentDictionary<string, int> _counts = new();
        public int Calls { get; private set; }
        public ICollection<string> Keys => _counts.Keys;
        public ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit, int windowSeconds, int burstBalance, string? connection, CancellationToken cancellationToken = default)
        {
            Calls++;
            var count = _counts.AddOrUpdate(key, 1, (_, value) => value + 1);
            return ValueTask.FromResult(new DistributedRateLimitResult(count <= limit, limit, Math.Max(0, limit - count), count > limit ? 1 : 0));
        }
    }
}
