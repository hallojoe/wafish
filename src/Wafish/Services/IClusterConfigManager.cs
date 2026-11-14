using Wafish.Models;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>
/// Cluster CRUD operations on the dynamic config working set. All methods are thread-safe
/// via an externally-injected <see cref="System.Threading.SemaphoreSlim"/> shared with
/// the route config manager.
/// </summary>
internal interface IClusterConfigManager
{
    /// <summary>Add or update a cluster from a complete native <see cref="ClusterConfig"/>.</summary>
    Task<RouteOperationResult> TryAddClusterConfig(ClusterConfig cluster, string source, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Add or update a cluster with destinations dictionary.</summary>
    Task<RouteOperationResult> TryAddCluster(string clusterId, Dictionary<string, string> destinations,
        string? loadBalancingPolicy, Models.HealthCheckConfig? healthCheck, string source, string? createdBy,
        Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>Add a new cluster from a creation request.</summary>
    Task<RouteOperationResult> TryAddCluster(CreateClusterRequest request, string source, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Partially update an existing cluster.</summary>
    Task<RouteOperationResult> TryUpdateCluster(string clusterId, UpdateClusterRequest request, CancellationToken cancellationToken = default);

    /// <summary>Remove a cluster if no routes reference it.</summary>
    Task<RouteOperationResult> TryRemoveCluster(string clusterId, CancellationToken cancellationToken = default);

    /// <summary>Atomically rename a cluster and update all referencing routes.</summary>
    Task<RouteOperationResult> TryRenameCluster(string oldClusterId, string newClusterId,
        Dictionary<string, string> destinations, string? loadBalancingPolicy,
        Models.HealthCheckConfig? healthCheck, string source, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Get all current clusters from the proxy provider.</summary>
    IReadOnlyList<ClusterConfig> GetClusters();

    /// <summary>Get a specific cluster by ID.</summary>
    ClusterConfig? GetCluster(string clusterId);
}
