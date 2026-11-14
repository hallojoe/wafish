using System.Text.Json;
using Wafish.Models;
using Wafish.Plugin.Waf.Helpers;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.Waf;

/// <summary>Rejects invalid address rules while preparing a snapshot, before storage or activation.</summary>
public sealed class WafConfigurationCompiler : IRoutePluginCompiler
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public bool CanCompile(string pluginId) => pluginId.Equals("waf", StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        var config = JsonSerializer.Deserialize<WafBindingOptions>(binding.ConfigJson, Json)
            ?? throw new InvalidOperationException("WAF configuration is required.");
        if (config.IpWhitelist == null || config.IpBlacklist == null ||
            config.IpWhitelist.Concat(config.IpBlacklist).Any(pattern => pattern == null || !IpMatcher.IsValidPattern(pattern)))
        {
            throw new InvalidOperationException("WAF address rules require valid IP addresses, wildcards, or CIDR prefixes.");
        }

        WafFieldExclusions.Validate(config);
        return new(binding, PluginExecutionStage.PreProxy, config, route);
    }
}
