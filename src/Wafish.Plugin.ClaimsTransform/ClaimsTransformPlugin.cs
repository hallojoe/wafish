using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Plugins;

namespace Wafish.Plugin.ClaimsTransform;

public sealed class ClaimsTransformPlugin : IGatewayPlugin
{
    public string PluginId => "claims-transform";
    public string DisplayName => "Claims transform";
    public string Version => "1.0";
    public PluginManifest Manifest => new(PluginId, DisplayName, Version, [PluginScope.Route],
        [PluginCapability.RequestMiddleware], 150, new(RequestMiddleware: true),
        [new(1, "builtin://plugins/claims-transform/schemas/config-v1.json", """
        {"type":"object","additionalProperties":false,"required":["mappings"],"properties":{
          "pathTemplate":{"type":"string","maxLength":8192,"title":"Downstream path template"},
          "mappings":{"type":"array","minItems":1,"maxItems":32,"items":{"type":"object","additionalProperties":false,"required":["claim","target","name"],"properties":{
            "claim":{"type":"string","minLength":1,"maxLength":256},
            "target":{"type":"string","enum":["Header","Query","Path"],"default":"Header"},
            "name":{"type":"string","minLength":1,"maxLength":256},
            "required":{"type":"boolean","default":true}
          }}}
        }}
        """)], "Map authenticated claims into downstream headers, query parameters, and paths.")
        { RequiredHostCapabilities = [new("wafish.claims-transform")] };
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
}
