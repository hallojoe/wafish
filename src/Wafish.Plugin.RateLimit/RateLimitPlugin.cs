using Wafish.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Wafish.Plugin.RateLimit;

public class RateLimitPlugin : IGatewayPlugin
{
    public string PluginId => "rate-limit";
    public string DisplayName => "Rate Limiting";
    public string Version => "1.0";
    public PluginManifest Manifest => new(
        PluginId, DisplayName, Version,
        [PluginScope.Route],
        [PluginCapability.ProxyPipeline],
        200,
        new PluginResourceRequirements(RequestMiddleware: true),
        [new PluginSchemaReference(1, "builtin://plugins/rate-limit/schemas/config-v1.json", """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{"enabled":{"type":"boolean","default":true},"algorithm":{"type":"string","enum":["FixedWindow","SlidingWindow","TokenBucket","Concurrency"],"default":"FixedWindow"},"permitLimit":{"type":"integer","minimum":1,"default":100},"window":{"type":"string","minLength":1,"default":"1m"},"requestCost":{"type":"integer","minimum":1,"maximum":1000000,"default":1,"description":"Permits per request for token and window algorithms. Cannot exceed capacity."},"queueLimit":{"type":"integer","minimum":0,"default":0},"quotaGroup":{"type":"string","minLength":1,"maxLength":64,"pattern":"^[a-z][a-z0-9-]*$","description":"Optional shared local quota group. Members require identical effective policies."},"partitionKey":{"type":"string","enum":["IpAddress","UserId","Route","Global"],"default":"IpAddress"},"segmentsPerWindow":{"type":"integer","minimum":2,"maximum":100,"default":4},"tokenLimit":{"type":"integer","minimum":1,"default":100},"tokensPerPeriod":{"type":"integer","minimum":1,"default":100},"replenishmentPeriod":{"type":"string","minLength":1,"default":"1s"}}}
            """)],
        "Local request limiting with selectable algorithms and optional shared quota groups.");

    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) => proxyPipeline.UseMiddleware<RateLimitMiddleware>();

    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }

}
