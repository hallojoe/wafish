using Wafish.Models;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

public partial class DynamicYarpConfigService
{
    private const string RegistrationServiceKey = "wafish.registration.service";
    private const string RegistrationInstanceKey = "wafish.registration.instance";

    /// <summary>Atomically replaces only this instance's destinations in a shared service cluster.</summary>
    public Task<RouteOperationResult> RegisterInstanceAsync(string serviceId, string instanceId,
        ClusterConfig cluster, IReadOnlyList<RouteConfig> routes)
        => RegisterInstanceAsync(serviceId, instanceId, cluster, routes, CancellationToken.None);

    public Task<RouteOperationResult> RegisterInstanceAsync(string serviceId, string instanceId,
        ClusterConfig cluster, IReadOnlyList<RouteConfig> routes, CancellationToken cancellationToken)
        => RegisterInstanceCoreAsync(serviceId, instanceId, cluster, routes, null, cancellationToken);

    private async Task<RouteOperationResult> RegisterInstanceCoreAsync(string serviceId, string instanceId,
        ClusterConfig cluster, IReadOnlyList<RouteConfig> routes, LeaseRegistration? lease, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceId) || instanceId.Length is < 1 or > 128 ||
            instanceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
        {
            return new(false, "ServiceId and a valid InstanceId are required.");
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var mutation = BeginMutation(cancellationToken);
            var config = mutation.Config;
            var existing = config.Clusters.FirstOrDefault(c => c.Config.ClusterId.Equals(cluster.ClusterId, StringComparison.OrdinalIgnoreCase));
            if (existing != null && (!OwnedBy(existing.Config.Metadata, serviceId) || existing.Source != "grpc"))
            {
                return new(false, "The cluster belongs to another configuration owner.");
            }

            if (config.Clusters.Any(c => OwnedBy(c.Config.Metadata, serviceId) && !c.Config.ClusterId.Equals(cluster.ClusterId, StringComparison.OrdinalIgnoreCase)))
            {
                return new(false, "A registered service cannot move clusters while instances remain registered.");
            }

            var priorLease = existing?.Config.Destinations?.Values.FirstOrDefault(d => OwnedBy(d.Metadata, serviceId, instanceId) && HasLease(d));
            if (priorLease is not null && !LeaseExpired(priorLease) && !LeaseMatches(priorLease, lease?.Token))
                return new(false, "The current instance lease credential is required.");
            if (priorLease is not null && !LeaseExpired(priorLease) && IsDraining(priorLease))
                return new(false, "The instance is draining. Unregister it before replacement.");
            var leaseMetadata = lease is null ? null : CreateLeaseMetadata(lease);
            var destinations = existing?.Config.Destinations?.ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
            foreach (var key in destinations.Where(d => OwnedBy(d.Value.Metadata, serviceId, instanceId)).Select(d => d.Key).ToArray())
            {
                destinations.Remove(key);
            }

            var othersRemain = destinations.Count > 0;
            var oldRoutes = config.Routes.Where(r => OwnedBy(r.Config.Metadata, serviceId)).ToArray();
            if (othersRemain && (existing!.Config.LoadBalancingPolicy != cluster.LoadBalancingPolicy || oldRoutes.Length != routes.Count ||
                routes.Any(r => !oldRoutes.Any(old => old.Config.RouteId == r.RouteId && old.Config.Match.Path == r.Match.Path))))
            {
                return new(false, "All instances must use the same paths and load-balancing policy.");
            }

            foreach (var pair in cluster.Destinations ?? new Dictionary<string, DestinationConfig>())
            {
                destinations[instanceId.ToLowerInvariant() + ":" + pair.Key] = pair.Value with
                { Metadata = WithOwner(leaseMetadata, serviceId, instanceId) };
            }

            if (destinations.Count == 0)
            {
                return new(false, "At least one destination is required.");
            }

            var metadata = cluster.Metadata?.ToDictionary(p => p.Key, p => p.Value) ?? new();
            metadata[RegistrationServiceKey] = serviceId;
            var merged = cluster with
            {
                Destinations = destinations, Metadata = metadata,
                // A failed instance must leave the healthy peer available without retries.
                HealthCheck = cluster.HealthCheck ?? new global::Yarp.ReverseProxy.Configuration.HealthCheckConfig
                { Active = new global::Yarp.ReverseProxy.Configuration.ActiveHealthCheckConfig { Enabled = true, Interval = TimeSpan.FromSeconds(2), Timeout = TimeSpan.FromSeconds(1), Policy = "ConsecutiveFailures", Path = "/health" } }
            };
            if (existing == null)
            {
                config.Clusters.Add(new DynamicClusterConfig { Config = merged, Source = "grpc", CreatedBy = serviceId });
            }
            else
            {
                existing.Config = merged;
            }

            foreach (var route in routes)
            {
                var collision = config.Routes.FirstOrDefault(r => r.Config.RouteId.Equals(route.RouteId, StringComparison.OrdinalIgnoreCase));
                if (collision != null && !OwnedBy(collision.Config.Metadata, serviceId))
                {
                    return new(false, "A route belongs to another configuration owner.");
                }
            }
            config.Routes.RemoveAll(r => OwnedBy(r.Config.Metadata, serviceId));
            foreach (var route in routes)
            {
                config.Routes.Add(new DynamicRouteConfig
                {
                    Config = route with { Metadata = new Dictionary<string, string> { [RegistrationServiceKey] = serviceId } },
                    Source = "grpc", CreatedBy = serviceId,
                    RouteUid = oldRoutes.FirstOrDefault(r => r.Config.RouteId == route.RouteId)?.RouteUid ?? Guid.NewGuid().ToString("N")
                });
            }

            _auditLog.RecordSuccess("RegisterInstance", serviceId, instanceId, null, null, new { cluster.ClusterId });
            return await mutation.CommitAsync("RegisterInstance", serviceId);
        }
        finally { _semaphore.Release(); }
    }

    /// <summary>Removes this instance only. Shared routes remain until their final destination leaves.</summary>
    public Task<RouteOperationResult> UnregisterInstanceAsync(string serviceId, string instanceId)
        => UnregisterInstanceAsync(serviceId, instanceId, CancellationToken.None);

    public Task<RouteOperationResult> UnregisterInstanceAsync(string serviceId, string instanceId, CancellationToken cancellationToken)
        => UnregisterInstanceCoreAsync(serviceId, instanceId, null, cancellationToken);

    public Task<RouteOperationResult> UnregisterLeasedInstanceAsync(string serviceId, string instanceId, string leaseToken, CancellationToken cancellationToken = default)
        => UnregisterInstanceCoreAsync(serviceId, instanceId, leaseToken, cancellationToken);

    private async Task<RouteOperationResult> UnregisterInstanceCoreAsync(string serviceId, string instanceId, string? leaseToken, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var mutation = BeginMutation(cancellationToken);
            var config = mutation.Config;
            if (config.Clusters.SelectMany(c => c.Config.Destinations?.Values ?? []).Any(d => OwnedBy(d.Metadata, serviceId, instanceId) && HasLease(d) && !LeaseMatches(d, leaseToken)))
                return new(false, "The current instance lease credential is required.");
            foreach (var cluster in config.Clusters.Where(c => OwnedBy(c.Config.Metadata, serviceId)).ToArray())
            {
                var remaining = cluster.Config.Destinations?.Where(d => !OwnedBy(d.Value.Metadata, serviceId, instanceId))
                    .ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase) ?? new();
                if (remaining.Count == 0)
                {
                    config.Routes.RemoveAll(r => OwnedBy(r.Config.Metadata, serviceId));
                    if (config.Routes.Any(r => r.Config.ClusterId == cluster.Config.ClusterId))
                    {
                        cluster.Config = cluster.Config with { Destinations = remaining };
                    }
                    else
                    {
                        config.Clusters.Remove(cluster);
                    }
                }
                else
                {
                    cluster.Config = cluster.Config with { Destinations = remaining };
                }
            }
            _auditLog.RecordSuccess("UnregisterInstance", serviceId, instanceId, null, null, null);
            return await mutation.CommitAsync("UnregisterInstance", serviceId);
        }
        finally { _semaphore.Release(); }
    }

    public Task<RouteOperationResult> UpdateInstanceDestinationsAsync(string serviceId, string instanceId,
        IReadOnlyDictionary<string, string> addresses, long expectedVersion)
        => UpdateInstanceDestinationsAsync(serviceId, instanceId, addresses, expectedVersion, CancellationToken.None);

    public Task<RouteOperationResult> UpdateInstanceDestinationsAsync(string serviceId, string instanceId,
        IReadOnlyDictionary<string, string> addresses, long expectedVersion, CancellationToken cancellationToken)
        => UpdateInstanceCoreAsync(serviceId, instanceId, addresses, expectedVersion, null, cancellationToken);

    public Task<RouteOperationResult> UpdateLeasedInstanceDestinationsAsync(string serviceId, string instanceId,
        IReadOnlyDictionary<string, string> addresses, long expectedVersion, string leaseToken, CancellationToken cancellationToken = default)
        => UpdateInstanceCoreAsync(serviceId, instanceId, addresses, expectedVersion, leaseToken, cancellationToken);

    private async Task<RouteOperationResult> UpdateInstanceCoreAsync(string serviceId, string instanceId,
        IReadOnlyDictionary<string, string> addresses, long expectedVersion, string? leaseToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || addresses.Count == 0 ||
            addresses.Any(p => string.IsNullOrWhiteSpace(p.Key) || !Uri.TryCreate(p.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            return new(false, "InstanceId and valid HTTP destinations are required.");
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_state.Version != expectedVersion)
            {
                return new(false, "Configuration revision changed. Register again before updating destinations.")
                { Status = ConfigurationUpdateStatus.VersionConflict, ActiveVersion = _state.Version, PersistedVersion = _state.PersistedVersion };
            }

            using var mutation = BeginMutation(cancellationToken);
            var cluster = mutation.Config.Clusters.FirstOrDefault(c => c.Source == "grpc" && OwnedBy(c.Config.Metadata, serviceId));
            if (cluster?.Config.Destinations?.Values.Any(d => OwnedBy(d.Metadata, serviceId, instanceId)) != true)
            {
                return new(false, "The instance is not registered for this service.");
            }

            var owned = cluster.Config.Destinations.Values.First(d => OwnedBy(d.Metadata, serviceId, instanceId));
            if (HasLease(owned) && (!LeaseMatches(owned, leaseToken) || LeaseExpired(owned) || IsDraining(owned)))
                return new(false, "A current, active instance lease is required.");
            var destinations = cluster.Config.Destinations.Where(d => !OwnedBy(d.Value.Metadata, serviceId, instanceId))
                .ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, address) in addresses)
            {
                var destinationId = instanceId.ToLowerInvariant() + ":" + key;
                if (destinations.ContainsKey(destinationId))
                {
                    return new(false, "Destination belongs to another instance.");
                }

                destinations[destinationId] = new DestinationConfig { Address = address,
                    Metadata = WithOwner(owned.Metadata, serviceId, instanceId) };
            }
            cluster.Config = cluster.Config with { Destinations = destinations };
            return await mutation.CommitAsync("UpdateInstanceDestinations", serviceId);
        }
        finally { _semaphore.Release(); }
    }

    public bool IsInstanceRegistered(string serviceId, string instanceId) => _state.Config.Clusters
        .Any(c => c.Config.Destinations?.Values.Any(d => OwnedBy(d.Metadata, serviceId, instanceId)) == true);

    private static bool OwnedBy(IReadOnlyDictionary<string, string>? metadata, string serviceId, string? instanceId = null) =>
        metadata != null && metadata.TryGetValue(RegistrationServiceKey, out var service) && service.Equals(serviceId, StringComparison.OrdinalIgnoreCase) &&
        (instanceId == null || metadata.TryGetValue(RegistrationInstanceKey, out var instance) && instance.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
}
