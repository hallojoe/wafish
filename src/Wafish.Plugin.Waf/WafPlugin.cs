using Wafish.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Wafish.Plugin.Waf;

public class WafPlugin : IGatewayPlugin
{
    public string PluginId => "waf";
    public string DisplayName => "Web Application Firewall";
    public string Version => "1.0";
    public PluginManifest Manifest => new(
        PluginId, DisplayName, Version,
        [PluginScope.Route],
        [PluginCapability.RequestMiddleware, PluginCapability.Dashboard],
        100,
        new PluginResourceRequirements(RequestMiddleware: true, Database: true),
        [new PluginSchemaReference(1, "builtin://plugins/waf/schemas/config-v1.json", """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{"detectionOnlyRules":{"type":"array","maxItems":4,"uniqueItems":true,"items":{"enum":["sql-injection","xss","path-traversal","ip-access"]}},"exclusions":{"type":"array","maxItems":32,"items":{"type":"object","additionalProperties":false,"required":["ruleId","target","field"],"properties":{"ruleId":{"enum":["sql-injection","xss"]},"target":{"enum":["Query","Form","Json"]},"field":{"type":"string","minLength":1,"maxLength":256}}}},"enabled":{"type":"boolean","default":true},"enableIpCheck":{"type":"boolean","default":true},"enableRequestSizeValidation":{"type":"boolean","default":true},"enableSqlInjectionDetection":{"type":"boolean","default":true},"enableXssDetection":{"type":"boolean","default":true},"enablePathTraversalDetection":{"type":"boolean","default":true},"ipWhitelist":{"type":"array","items":{"type":"string","format":"ip-pattern"},"uniqueItems":true,"default":[]},"ipBlacklist":{"type":"array","items":{"type":"string","format":"ip-pattern"},"uniqueItems":true,"default":[]},"maxRequestBodySize":{"type":"integer","minimum":0,"default":10485760},"maxHeaderCount":{"type":"integer","minimum":0,"default":64},"maxHeaderSize":{"type":"integer","minimum":0,"default":8192}}}
            """)],
        "Route-scoped request inspection, blocking, audit, and configuration management.");

    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) => app.UseMiddleware<WafMiddleware>();
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }

    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }

}
