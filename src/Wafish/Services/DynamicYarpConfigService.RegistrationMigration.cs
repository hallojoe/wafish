using Wafish.Models;

namespace Wafish.Services;

public partial class DynamicYarpConfigService
{
    // Persist the renamed keys in the existing atomic startup transaction.
    private static void MigrateRegistrationMetadata(GatewayDynamicConfig config)
    {
        foreach (var route in config.Routes.Where(r => r.Source == "grpc"))
            route.Config = route.Config with { Metadata = MigrateRegistrationMetadata(route.Config.Metadata) };
        foreach (var cluster in config.Clusters.Where(c => c.Source == "grpc"))
            cluster.Config = cluster.Config with
            {
                Metadata = MigrateRegistrationMetadata(cluster.Config.Metadata),
                Destinations = cluster.Config.Destinations!.ToDictionary(p => p.Key,
                    p => p.Value with { Metadata = MigrateRegistrationMetadata(p.Value.Metadata) })
            };
    }

    private static IReadOnlyDictionary<string, string>? MigrateRegistrationMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        var migrated = metadata.ToDictionary(p => p.Key, p => p.Value);
        foreach (var suffix in new[] { "service", "instance", "lease-hash", "lease-expiry", "lease-duration", "draining" })
        {
            if (!migrated.Remove("aneiang.registration." + suffix, out var value)) continue;
            var key = "wafish.registration." + suffix;
            if (migrated.TryGetValue(key, out var current) && current != value)
                throw new InvalidOperationException("Conflicting legacy and current registration metadata: " + key);
            migrated[key] = value;
        }
        return migrated;
    }
}
