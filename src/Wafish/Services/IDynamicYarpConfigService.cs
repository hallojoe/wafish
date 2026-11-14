using Wafish.Models;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>
/// Interface for dynamic YARP configuration management at runtime.
/// Provides thread-safe operations for routes, clusters, health check, heartbeat, and metadata.
/// </summary>
public interface IDynamicYarpConfigService
{
    /// <summary>Add or update a route (creates or replaces cluster). Thread-safe.</summary>
    Task<RouteOperationResult> TryAddRoute(RegisterRouteRequest request, string source = "dynamic", string? createdBy = null);

    Task<RouteOperationResult> TryAddRoute(RegisterRouteRequest request, string source, string? createdBy, CancellationToken cancellationToken);

    /// <summary>
    /// Add or update a route from a complete native YARP <see cref="RouteConfig"/>, preserving all
    /// advanced properties (full Match criteria, Auth/Cors/RateLimiter/Timeout policies, etc.).
    /// </summary>
    Task<RouteOperationResult> TryAddRouteConfig(RouteConfig route, string source = "dashboard", string? createdBy = "dashboard-user");

    Task<RouteOperationResult> TryAddRouteConfig(RouteConfig route, string source, string? createdBy, CancellationToken cancellationToken);

    /// <summary>
    /// Add or update a cluster from a complete native YARP <see cref="ClusterConfig"/>, preserving all
    /// advanced properties (SessionAffinity, HttpClient, HttpRequest, per-destination metadata, etc.).
    /// </summary>
    Task<RouteOperationResult> TryAddClusterConfig(ClusterConfig cluster, string source = "dashboard", string? createdBy = "dashboard-user");

    Task<RouteOperationResult> TryAddClusterConfig(ClusterConfig cluster, string source, string? createdBy, CancellationToken cancellationToken);

    /// <summary>Remove a route and optionally its orphaned cluster.</summary>
    Task<RouteOperationResult> TryRemoveRoute(string routeName, string? clientIp = null, bool removeOrphanedCluster = true);

    Task<RouteOperationResult> TryRemoveRoute(string routeName, string? clientIp, bool removeOrphanedCluster, CancellationToken cancellationToken);

    /// <summary>Add or update a cluster with destinations.</summary>
    Task<RouteOperationResult> TryAddCluster(string clusterId, Dictionary<string, string> destinations,
        string? loadBalancingPolicy = null, Models.HealthCheckConfig? healthCheck = null,
        string source = "dynamic", string? createdBy = null,
        Dictionary<string, string>? metadata = null);

    Task<RouteOperationResult> TryAddCluster(string clusterId, Dictionary<string, string> destinations,
        string? loadBalancingPolicy, Models.HealthCheckConfig? healthCheck,
        string source, string? createdBy,
        Dictionary<string, string>? metadata, CancellationToken cancellationToken);

    /// <summary>Add a new cluster from a creation request.</summary>
    Task<RouteOperationResult> TryAddCluster(CreateClusterRequest request, string source = "dynamic", string? createdBy = null);

    Task<RouteOperationResult> TryAddCluster(CreateClusterRequest request, string source, string? createdBy, CancellationToken cancellationToken);

    /// <summary>Partially update an existing cluster.</summary>
    Task<RouteOperationResult> TryUpdateCluster(string clusterId, UpdateClusterRequest request);

    Task<RouteOperationResult> TryUpdateCluster(string clusterId, UpdateClusterRequest request, CancellationToken cancellationToken);

    /// <summary>Remove a cluster if no routes reference it.</summary>
    Task<RouteOperationResult> TryRemoveCluster(string clusterId);

    Task<RouteOperationResult> TryRemoveCluster(string clusterId, CancellationToken cancellationToken);

    /// <summary>Atomically rename a cluster and update all referencing routes.</summary>
    Task<RouteOperationResult> TryRenameCluster(string oldClusterId, string newClusterId,
        Dictionary<string, string> destinations, string? loadBalancingPolicy = null,
        Models.HealthCheckConfig? healthCheck = null, string source = "dashboard", string? createdBy = "dashboard-user");

    Task<RouteOperationResult> TryRenameCluster(string oldClusterId, string newClusterId,
        Dictionary<string, string> destinations, string? loadBalancingPolicy,
        Models.HealthCheckConfig? healthCheck, string source, string? createdBy, CancellationToken cancellationToken);

    /// <summary>Get all current routes.</summary>
    IReadOnlyList<RouteConfig> GetRoutes();

    /// <summary>Get all current clusters.</summary>
    IReadOnlyList<ClusterConfig> GetClusters();

    /// <summary>Get a specific cluster by ID.</summary>
    ClusterConfig? GetCluster(string clusterId);

    /// <summary>Get dynamic configuration metadata.</summary>
    GatewayDynamicConfig? GetDynamicConfig();

    /// <summary>Re-apply dynamic config to YARP in-memory provider.</summary>
    void RefreshConfig();

    Task RefreshConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>Save dynamic configuration to persistence.</summary>
    Task SaveDynamicConfig();

    Task SaveDynamicConfig(CancellationToken cancellationToken);

    /// <summary>Replace entire configuration in one batch operation (used for rollback).</summary>
    Task ReplaceAllConfig(IReadOnlyList<RouteConfig> newRoutes, IReadOnlyList<ClusterConfig> newClusters,
        string source = "rollback", string? createdBy = "dashboard-user", IReadOnlyDictionary<string, bool>? routeActivation = null);

    Task ReplaceAllConfig(IReadOnlyList<RouteConfig> newRoutes, IReadOnlyList<ClusterConfig> newClusters,
        string source, string? createdBy, IReadOnlyDictionary<string, bool>? routeActivation, CancellationToken cancellationToken);

    /// <summary>
    /// Batch add-or-update clusters and routes in a single lock/persist/publish cycle.
    /// Used by config import so N items cost one full save + one publish instead of N.
    /// </summary>
    Task<RouteOperationResult> ImportBatchAsync(
        IReadOnlyList<ClusterConfig> clusters, IReadOnlyList<RouteConfig> routes,
        string source = "import", string? createdBy = "dashboard-user", IReadOnlyDictionary<string, bool>? routeActivation = null);

    Task<RouteOperationResult> ImportBatchAsync(
        IReadOnlyList<ClusterConfig> clusters, IReadOnlyList<RouteConfig> routes,
        string source, string? createdBy, IReadOnlyDictionary<string, bool>? routeActivation, CancellationToken cancellationToken);

    /// <summary>Update heartbeat timestamp for a registered service.</summary>
    bool UpdateHeartbeat(string routeName, string? clientIp = null);

    /// <summary>Atomically rename a route (repoint references + delete old).</summary>
    Task<RouteOperationResult> TryRenameRoute(string oldRouteId, string newRouteId, RegisterRouteRequest request,
        string source = "dashboard", string? createdBy = "dashboard-user");

    Task<RouteOperationResult> TryRenameRoute(string oldRouteId, string newRouteId, RegisterRouteRequest request,
        string source, string? createdBy, CancellationToken cancellationToken);

    /// <summary>Enable or disable a route. Disabled routes are retained but excluded from forwarding.</summary>
    Task<RouteOperationResult> TrySetRouteEnabled(string routeId, bool enabled, string? createdBy = "dashboard-user");

    Task<RouteOperationResult> TrySetRouteEnabled(string routeId, bool enabled, string? createdBy, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically delete multiple clusters under a single lock/persist/publish cycle.
    /// Clusters referenced by any route are refused (per-item failure) and left intact.
    /// </summary>
    Task<BatchOperationResult> BatchDeleteClustersAsync(
        IReadOnlyList<string> clusterIds, string? createdBy = "dashboard-user");

    Task<BatchOperationResult> BatchDeleteClustersAsync(
        IReadOnlyList<string> clusterIds, string? createdBy, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically delete multiple routes under a single lock/persist/publish cycle.
    /// Optionally removes clusters left orphaned by the deletion.
    /// </summary>
    Task<BatchOperationResult> BatchDeleteRoutesAsync(
        IReadOnlyList<string> routeIds, bool removeOrphanedClusters = false,
        string? createdBy = "dashboard-user");

    Task<BatchOperationResult> BatchDeleteRoutesAsync(
        IReadOnlyList<string> routeIds, bool removeOrphanedClusters,
        string? createdBy, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically enable or disable multiple routes under a single lock/persist/publish cycle.
    /// </summary>
    Task<BatchOperationResult> BatchSetRoutesEnabledAsync(
        IReadOnlyList<string> routeIds, bool enabled, string? createdBy = "dashboard-user");

    Task<BatchOperationResult> BatchSetRoutesEnabledAsync(
        IReadOnlyList<string> routeIds, bool enabled, string? createdBy, CancellationToken cancellationToken);
}
