using Wafish.Models;
using Wafish.Storage;
namespace Wafish.Services;

internal sealed class DynamicConfigPersister(IConfigurationSnapshotRepository snapshots) : IDynamicConfigPersister
{
    public async Task<GatewayDynamicConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await snapshots.LoadAsync(cancellationToken);
        var config = new GatewayDynamicConfig { Version = snapshot.Version, Routes = snapshot.Routes.ToRouteConfigs() };
        foreach (var entity in snapshot.Clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cluster = entity.ToClusterConfig();
            cluster.Config = cluster.Config with
            {
                Destinations = snapshot.Destinations.Where(d => d.ClusterId == entity.ClusterId).ToList().ToDestinations()
            };
            config.Clusters.Add(cluster);
        }
        return config;
    }

    public Task SaveAsync(GatewayDynamicConfig config, string operationName, string? targetName = null)
        => snapshots.SaveAsync(new ConfigurationSnapshot(config.Version,
            config.Routes.Select(r => r.ToEntity()).ToList(),
            config.Clusters.Select(c => c.ToEntity()).ToList(),
            config.Clusters.SelectMany(c => (c.Config.Destinations ?? new Dictionary<string, global::Yarp.ReverseProxy.Configuration.DestinationConfig>())
                .Select(d => d.ToEntity(c.Config.ClusterId))).ToList()), config.Version - 1);
}
