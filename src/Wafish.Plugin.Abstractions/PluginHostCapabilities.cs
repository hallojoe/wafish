using System.Collections.Frozen;

namespace Wafish.Plugins;

/// <summary>Minimum supported contract version for a named host capability. Versions start at one.</summary>
public sealed record PluginHostCapabilityRequirement(string Name, int MinimumVersion = 1);

/// <summary>Capabilities implemented by this runtime version. Version increments preserve earlier capability contracts.</summary>
public static class PluginHostCapabilities
{
    public static IReadOnlyDictionary<string, int> Versions { get; } = new Dictionary<string, int>
    {
        ["wafish.runtime"] = 1,
        ["wafish.redirect-endpoint"] = 1,
        ["wafish.proxy-pipeline"] = 1,
        ["wafish.resource-lifecycle"] = 1
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
