using Wafish.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Wafish.Plugin.RateLimit.Redis;

/// <summary>
/// Route-scoped distributed rate limiting backed by Redis. Counters are shared across all
/// gateway instances, which keeps limits precise in horizontally scaled deployments.
/// Routes select FailClosed (default) or FailOpen when Redis is unavailable.
/// </summary>
public class RedisRateLimitPlugin : IGatewayPlugin
{
    public string PluginId => "rate-limit-redis";
    public string DisplayName => "Distributed Rate Limit (Redis)";
    public string Version => "1.0";
    public PluginManifest Manifest => new(
        PluginId, DisplayName, Version,
        [PluginScope.Route],
        [PluginCapability.ProxyPipeline],
        210,
        new PluginResourceRequirements(RequestMiddleware: true),
        [new PluginSchemaReference(1, "builtin://plugins/rate-limit-redis/schemas/config-v1.json", """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{"enabled":{"type":"boolean","default":true},"redisConnectionString":{"type":"string","minLength":1,"title":"Redis connection string","description":"e.g. localhost:6379 or user:pass@host:6379,ssl=True"},"failureMode":{"type":"string","enum":["FailClosed","FailOpen"],"default":"FailClosed"},"algorithm":{"type":"string","enum":["FixedWindow","SlidingWindow","TokenBucket","DualWindow"],"default":"FixedWindow"},"limit":{"type":"integer","minimum":1,"default":100},"windowSeconds":{"type":"integer","minimum":1,"default":60},"keyPrefix":{"type":"string","minLength":1,"default":"wafish:rl"},"burstLimit":{"type":"integer","minimum":1,"maximum":1000000,"description":"DualWindow only: requests per burst window"},"burstWindowSeconds":{"type":"integer","minimum":1,"maximum":86400,"description":"DualWindow only: window shorter than windowSeconds"},"burstBalance":{"type":"integer","minimum":0,"default":0,"description":"TokenBucket only: extra burst capacity above limit"},"tenantPartition":{"type":["object","null"],"additionalProperties":false,"required":["claimType","claimIssuer","authenticationType","allowedTenants"],"properties":{"claimType":{"type":"string","minLength":1,"maxLength":256},"claimIssuer":{"type":"string","minLength":1,"maxLength":512},"authenticationType":{"type":"string","minLength":1,"maxLength":128},"allowedTenants":{"type":"array","minItems":1,"maxItems":1024,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":128}}}}}}
            """)],
        "Route-scoped distributed rate limiting with Redis-backed atomic counters.");

    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null)
    {
        services.AddOptions<RedisRateLimitStoreOptions>().BindConfiguration("Gateway:RateLimit:Redis");
        services.AddSingleton<IDistributedRateLimitStore, RedisLuaRateLimitStore>();
    }

    public void ConfigureMiddleware(IApplicationBuilder app) { }

    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) =>
        proxyPipeline.UseMiddleware<RedisRateLimitMiddleware>();

    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }
}
