using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>One preparation/commit gate per publisher. Hold it until activation completes.</summary>
public sealed class GatewayPublicationCoordinator
{
    private static readonly ConditionalWeakTable<IGatewaySnapshotPublisher, GatewayPublicationCoordinator> Coordinators = new();
    private readonly Dictionary<string, (PluginBindingSnapshot Binding, IReadOnlyDictionary<string, DestinationConfig> Destinations)> _discoveries = new(StringComparer.Ordinal);
    public long? ActiveBindingRevision { get; set; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public static GatewayPublicationCoordinator For(IGatewaySnapshotPublisher publisher) => Coordinators.GetValue(publisher, _ => new());

    public void RememberDiscovery(string clusterId, PluginBindingSnapshot binding, IReadOnlyDictionary<string, DestinationConfig> destinations)
        => _discoveries[clusterId] = (binding, destinations);

    public GatewaySnapshot PreserveDiscovery(GatewaySnapshot candidate)
    {
        var clusters = candidate.Clusters.Select(cluster =>
        {
            if (!_discoveries.TryGetValue(cluster.ClusterId, out var discovered))
            {
                return cluster;
            }

            var binding = DiscoveryBinding(candidate, cluster.ClusterId);
            if (binding == discovered.Binding)
            {
                return cluster with { Destinations = discovered.Destinations };
            }

            return cluster;
        }).ToImmutableArray();
        return candidate with { Clusters = clusters };
    }

    public void Published(GatewaySnapshot snapshot)
    {
        foreach (var (clusterId, discovery) in _discoveries.ToArray())
        {
            if (DiscoveryBinding(snapshot, clusterId) != discovery.Binding)
            {
                _discoveries.Remove(clusterId);
            }
        }
    }

    public static PluginBindingSnapshot? DiscoveryBinding(GatewaySnapshot snapshot, string clusterId) =>
        snapshot.ClusterPlugins.TryGetValue(clusterId, out var bindings)
            ? bindings.FirstOrDefault(binding => binding.PluginId == "service-discovery") : null;
}
