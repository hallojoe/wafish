using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Plugins;

namespace Wafish.Plugin.Aggregation;

public sealed class AggregationPlugin : IGatewayPlugin
{
    public string PluginId=>"request-aggregation";
    public string DisplayName=>"JSON request aggregation";
    public string Version=>"1.0";
    public PluginManifest Manifest=>new(PluginId,DisplayName,Version,[PluginScope.Route],
        [PluginCapability.RequestMiddleware],60,new(RequestMiddleware:true),
        [new(1,"builtin://plugins/request-aggregation/schemas/config-v1.json","""
        {"type":"object","additionalProperties":false,"required":["children"],"properties":{
          "maxChildren":{"type":"integer","minimum":1,"maximum":8,"default":4},
          "parallelism":{"type":"integer","minimum":1,"maximum":8,"default":4},
          "timeoutSeconds":{"type":"integer","minimum":1,"maximum":60,"default":10},
          "maxChildBytes":{"type":"integer","minimum":1,"maximum":4194304,"default":1048576},
          "maxCombinedBytes":{"type":"integer","minimum":1,"maximum":16777216,"default":4194304},
          "children":{"type":"array","minItems":1,"maxItems":8,"default":[],"items":{"type":"object","additionalProperties":false,"required":["name","routeUid","path"],"properties":{
            "name":{"type":"string","minLength":1,"maxLength":64},
            "routeUid":{"type":"string","title":"Child route","format":"wafish-route-uid","minLength":1,"maxLength":256},
            "path":{"type":"string","title":"Child path","description":"Local path. Use {route.name} or {query.name} for parent values.","minLength":1,"maxLength":4096},
            "query":{"type":"array","maxItems":32,"default":[],"items":{"type":"object","additionalProperties":false,"required":["name","value"],"properties":{
              "name":{"type":"string","minLength":1,"maxLength":64},"value":{"type":"string","minLength":1,"maxLength":4096}
            }}},
            "forwardHeaders":{"type":"array","maxItems":32,"uniqueItems":true,"default":[],"items":{"type":"string"}}
          }}}
        }}
        """)],"Combine independent GET JSON routes with bounded parallel execution.")
        {RequiredHostCapabilities=[new("wafish.request-aggregation")]};
    public void ConfigureServices(IServiceCollection services,object? pluginOptions=null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
}
