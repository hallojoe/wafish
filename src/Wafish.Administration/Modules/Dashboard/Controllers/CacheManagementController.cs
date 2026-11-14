using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Plugin.Cache;
using Wafish.Services;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>Optional route and tag selectors. Supplying both selects their intersection.</summary>
public sealed record CachePurgeRequest(string? RouteId = null, string? Tag = null);

[ApiController]
[Route("api/cache/purge")]
[DashboardPermission(DashboardPermission.PluginManagement)]
public sealed class CacheManagementController(ResponseCacheStore cache, IConfigChangeAuditLog audit) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(4096)]
    public IActionResult Purge(CachePurgeRequest request)
    {
        try
        {
            var removed = cache.Purge(request.RouteId, request.Tag);
            audit.RecordSuccess("CachePurge", "response-cache", DashboardPermissionService.Actor(HttpContext), after: new { request.RouteId, request.Tag, removed });
            return Ok(new { code = 200, data = new { removed } });
        }
        catch (ArgumentException error) { return BadRequest(new { code = 400, message = error.Message }); }
    }
}
