using System.Text.Json;
using Wafish.Models;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.RateLimit.Redis;

/// <summary>Validates combined quota settings before persistence or activation.</summary>
public sealed class RedisRateLimitCompiler : IRoutePluginCompiler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanCompile(string pluginId) => pluginId.Equals("rate-limit-redis", StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        var config = RedisDualWindowPolicy.Compile(JsonSerializer.Deserialize<RedisRateLimitExecutionConfig>(binding.ConfigJson, Json)
            ?? throw new ArgumentException("Redis rate-limit configuration is required."));
        if (string.Equals(config.Algorithm, "DualWindow", StringComparison.OrdinalIgnoreCase) && binding.SchemaVersion != 1)
            throw new ArgumentException("DualWindow requires schema 1.");
        return new(binding, PluginExecutionStage.RateLimit, config, route);
    }
}
