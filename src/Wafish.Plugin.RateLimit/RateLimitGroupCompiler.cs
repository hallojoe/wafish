using System.Text.Json;
using System.Text.Json.Serialization;
using Wafish.Infrastructure.State;
using Wafish.Models;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.RateLimit;

/// <summary>Validates shared policy ownership before configuration persistence.</summary>
public sealed class RateLimitGroupCompiler(IRateLimiterStore? store = null) : IRoutePluginCompiler, IGatewaySnapshotValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public bool CanCompile(string pluginId) => pluginId.Equals("rate-limit", StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        var config = LocalQuotaGroupPolicy.Compile(JsonSerializer.Deserialize<RateLimitExecutionConfig>(binding.ConfigJson, Json)
            ?? throw new ArgumentException("Rate-limit configuration is required."));
        if (config.QuotaGroup != null && (binding.SchemaVersion != 1 || store is not IGroupedRateLimiterStore))
            throw new ArgumentException("Named quotas require schema 1 and a group-capable local limiter store.");
        return new(binding, PluginExecutionStage.RateLimit, config, route);
    }
    public void Validate(GatewaySnapshot snapshot)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var plan in snapshot.RouteExecutionPlans.Values)
        {
            var configs = plan.Plugins.Where(p => CanCompile(p.Binding.PluginId)).Select(p => p.RuntimeConfig).OfType<RateLimitExecutionConfig>().ToArray();
            if (configs.Length > 1 && configs.Any(c => c.QuotaGroup != null))
                throw new ArgumentException("A route with a named quota requires exactly one enabled rate-limit binding.");
            foreach (var config in configs)
            {
                if (!config.Enabled || config.QuotaGroup is not { } group) continue;
                var policy = config.QuotaGroupPolicy!;
                if (groups.TryGetValue(group, out var previous) && previous != policy)
                    throw new ArgumentException($"Quota group '{group}' has conflicting route policies.");
                if (store is not IGroupedRateLimiterStore grouped || !grouped.IsGroupPolicyCompatible(group, policy))
                    throw new ArgumentException($"Quota group '{group}' retains counters under another policy. Drain and retire them before changing the policy.");
                groups[group] = policy;
            }
        }
    }
}
