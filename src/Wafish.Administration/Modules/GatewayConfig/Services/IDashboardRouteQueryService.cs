using Wafish.Dashboard.Modules.Dashboard.Models;

namespace Wafish.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Service for querying route information.
/// </summary>
public interface IDashboardRouteQueryService
{
    /// <summary>
    /// Gets all routes with their configurations and match criteria.
    /// </summary>
    /// <returns>List of route responses.</returns>
    IReadOnlyList<DashboardRouteResponse> GetRoutes();
}
