using System.Text.Json;
using System.Text.Json.Serialization;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.Redirect;

public sealed class RedirectConfigurationCompiler : IRoutePluginCompiler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public bool CanCompile(string pluginId) => pluginId.Equals("redirect", StringComparison.OrdinalIgnoreCase);

    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        if (binding.SchemaVersion != 1) throw new ArgumentException("Unsupported redirect schema version.");
        var options = JsonSerializer.Deserialize<RedirectOptions>(binding.ConfigJson, Json)
            ?? throw new ArgumentException("Redirect configuration is required.");
        return new(binding, PluginExecutionStage.Redirect, RedirectEvaluator.Compile(options, route), route);
    }
}
