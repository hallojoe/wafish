using Wafish.Models;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>
/// Route CRUD operations on the dynamic config working set. All methods are thread-safe
/// via an externally-injected <see cref="System.Threading.SemaphoreSlim"/> shared with
/// the cluster config manager.
/// </summary>
internal interface IRouteConfigManager
{
    /// <summary>Add or update a route from a simplified registration request.</summary>
    Task<RouteOperationResult> TryAddRoute(RegisterRouteRequest request, string source, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Add or update a route from a complete native <see cref="RouteConfig"/>.</summary>
    Task<RouteOperationResult> TryAddRouteConfig(RouteConfig route, string source, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Remove a route and optionally its orphaned cluster.</summary>
    Task<RouteOperationResult> TryRemoveRoute(string routeName, string? clientIp, bool removeOrphanedCluster, CancellationToken cancellationToken = default);

    /// <summary>Atomically rename a route (remove old, add new).</summary>
    Task<RouteOperationResult> TryRenameRoute(string oldRouteId, string newRouteId, RegisterRouteRequest request, string source, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Enable or disable a route. Disabled routes are retained but excluded from forwarding.</summary>
    Task<RouteOperationResult> TrySetRouteEnabled(string routeId, bool enabled, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Get all current routes from the proxy provider.</summary>
    IReadOnlyList<RouteConfig> GetRoutes();

    /// <summary>Get the dynamic config's route list directly (used by ReplaceAllConfig).</summary>
    GatewayDynamicConfig GetDynamicConfig();
}
