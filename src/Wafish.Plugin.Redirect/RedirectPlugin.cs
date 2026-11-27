using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Plugins;

namespace Wafish.Plugin.Redirect;

public sealed class RedirectPlugin : IGatewayPlugin
{
    public string PluginId => "redirect";
    public string DisplayName => "Redirect";
    public string Version => "1.0";
    public PluginManifest Manifest => new PluginManifest(PluginId, DisplayName, Version,
        [PluginScope.Route], [PluginCapability.RequestMiddleware], 250,
        new PluginResourceRequirements(RequestMiddleware: true),
        [new PluginSchemaReference(1, "builtin://plugins/redirect/schemas/config-v1.json", """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{
          "mode":{"type":"string","enum":["Target","HttpsOnly"],"default":"Target"},
          "target":{"type":"string","maxLength":8192,"description":"Local path or HTTP(S) URL. Supports {route.name}, {**route.name}, {request.host}, and {request.path}."},
          "statusCode":{"type":"integer","enum":[301,302,303,307,308],"default":307,"description":"307 is temporary and preserves the method. 308 is permanent and preserves the method. 301/302 may change POST to GET; 303 directs retrieval."},
          "queryMode":{"type":"string","enum":["Discard","Preserve"],"default":"Discard"},
          "allowedHosts":{"type":"array","maxItems":128,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":253},"default":[],"description":"Exact hostnames required for variable hosts and HTTPS mode. No ports or wildcards."},
          "httpsPort":{"type":"integer","minimum":1,"maximum":65535,"default":443}
        }}
        """)], "Route redirects with validated targets, host templates, and HTTPS enforcement.")
        { RequiredHostCapabilities = [new("wafish.redirect-endpoint")] };
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
}
