using Wafish.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Wafish.Plugin.Cache;

public sealed class ResponseCachePlugin : IGatewayPlugin
{
    public string PluginId => "response-cache";
    public string DisplayName => "Response Cache";
    public string Version => "1.0";
    public PluginManifest Manifest => new(PluginId, DisplayName, Version, [PluginScope.Route], [PluginCapability.ProxyPipeline], 300,
        new PluginResourceRequirements(RequestMiddleware: true),
        [new PluginSchemaReference(1, "builtin://plugins/response-cache/schemas/config-v1.json", """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["enabled"],"properties":{"tags":{"type":"array","maxItems":8,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":64}},"enabled":{"type":"boolean","default":true},"ttlSeconds":{"type":"integer","minimum":1,"maximum":86400,"default":60},"maxBodyBytes":{"type":"integer","minimum":1024,"maximum":10485760,"default":1048576},"varyByQuery":{"type":"boolean","default":true,"deprecated":true,"description":"Deprecated and ignored. Cache keys always include the complete query string for response isolation."},"varyHeaders":{"type":"array","items":{"type":"string","minLength":1},"uniqueItems":true,"default":[],"deprecated":true,"description":"Deprecated and ignored. Cache keys include all request headers except Cache-Control, Pragma, X-Request-Id, traceparent, tracestate, and baggage. Responses varying on excluded headers bypass storage."},"cacheStatusCodes":{"type":"array","items":{"type":"integer","minimum":200,"maximum":599},"uniqueItems":true,"default":[200]}}}
        """)], "Route-scoped bounded in-memory HTTP response cache.");
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) => proxyPipeline.UseMiddleware<ResponseCacheMiddleware>();

    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }

}
