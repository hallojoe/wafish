using System.Collections.Frozen;

namespace Wafish.Plugins;

public enum PluginStatisticKind { Counter, Gauge }

/// <summary>Unit uses UCUM notation where available. Reset describes the lifetime of cumulative values.</summary>
public sealed record PluginStatisticDescriptor(string Unit, PluginStatisticKind Kind, string Reset, bool Estimated = false);

public static class PluginStatisticDescriptors
{
    public static IReadOnlyDictionary<string, PluginStatisticDescriptor> Standard { get; } = Create();

    public static IReadOnlyDictionary<string, PluginStatisticDescriptor> Describe(IReadOnlyDictionary<string, long> statistics)
        => statistics.Keys.Where(Standard.ContainsKey).ToFrozenDictionary(key => key, key => Standard[key], StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, PluginStatisticDescriptor> Create()
    {
        var descriptors = new Dictionary<string, PluginStatisticDescriptor>(StringComparer.Ordinal);
        foreach (var name in new[] { "submittedEntries", "writtenEntries", "droppedEntries", "failures", "quotaDiscardedBodies", "quotaDiscardedMetadata" })
            descriptors[name] = new("{count}", PluginStatisticKind.Counter, "Owning store or service recreation; process restart.");
        foreach (var name in new[] { "pendingEntries", "clusterCount", "partitionCount", "activeConnections", "entries" })
            descriptors[name] = new("{count}", PluginStatisticKind.Gauge, "Current value; no cumulative reset.");
        descriptors["memoryBytes"] = new("By", PluginStatisticKind.Gauge, "Current value; no cumulative reset.", Estimated: true);
        descriptors["quotaMetadataBytes"] = new("By", PluginStatisticKind.Gauge, "Last committed quota write; writer recreation.", Estimated: true);
        descriptors["quotaBodyBytes"] = new("By", PluginStatisticKind.Gauge, "Last committed quota write; writer recreation.", Estimated: true);
        descriptors["durationMicroseconds"] = new("us", PluginStatisticKind.Counter, "Owning store recreation; process restart.");
        return descriptors.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
