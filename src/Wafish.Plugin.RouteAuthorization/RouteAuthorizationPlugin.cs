using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Plugins;

namespace Wafish.Plugin.RouteAuthorization;

public sealed class RouteAuthorizationPlugin : IGatewayPlugin
{
    public string PluginId => "route-authorization";
    public string DisplayName => "Route authorization";
    public string Version => "1.0";
    public PluginManifest Manifest => new(PluginId, DisplayName, Version, [PluginScope.Route],
        [PluginCapability.RequestMiddleware], 50, new(RequestMiddleware: true),
        [new(1, "builtin://plugins/route-authorization/schemas/config-v1.json", """
        {"type":"object","additionalProperties":false,"properties":{
          "allowedScopes":{"type":"array","maxItems":32,"uniqueItems":true,"default":[],"items":{"type":"string","minLength":1,"maxLength":256}},
          "claims":{"type":"array","maxItems":32,"default":[],"items":{"type":"object","additionalProperties":false,"required":["type","allowedValues"],"properties":{
            "type":{"type":"string","minLength":1,"maxLength":256},
            "allowedValues":{"type":"array","minItems":1,"maxItems":32,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":1024}}
          }}}
        }}
        """)], "Require authenticated claims and allowed scopes before route responses.")
        { RequiredHostCapabilities = [new("wafish.route-authorization")] };
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
}
