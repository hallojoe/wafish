using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Infrastructure.State;
using Wafish.Services;

namespace Wafish.Dashboard.Modules.GatewayConfig.Controllers;

public sealed record DestinationMaintenanceRequest(string ClusterId, string DestinationId, string Reason, int DurationSeconds, bool AllowRecoveryProbes = false);

[ApiController]
[Route("api/circuit-breaker/maintenance")]
[RequestSizeLimit(8192)]
public sealed class DestinationMaintenanceController(IDestinationMaintenanceStore maintenance, IDynamicYarpConfigService configuration,
    IConfigChangeAuditLog audit) : ControllerBase
{
    [HttpGet]
    [DashboardPermission(DashboardPermission.Read)]
    public IActionResult Get() => Ok(maintenance.GetAll());

    [HttpPut]
    [DashboardPermission(DashboardPermission.PluginManagement)]
    public IActionResult Put(DestinationMaintenanceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cluster = configuration.GetDynamicConfig()?.Clusters.FirstOrDefault(c => c.Config.ClusterId == request.ClusterId);
        if (cluster?.Config.Destinations?.ContainsKey(request.DestinationId) != true)
            return NotFound(new { code = 404, message = "The destination does not exist." });
        try
        {
            var window = maintenance.Set(request.ClusterId, request.DestinationId, DashboardPermissionService.Actor(HttpContext),
                request.Reason, TimeSpan.FromSeconds(request.DurationSeconds), request.AllowRecoveryProbes);
            audit.RecordSuccess("DestinationMaintenance", request.ClusterId + "/" + request.DestinationId, window.Actor, after: window);
            return Ok(window);
        }
        catch (ArgumentException error) { return BadRequest(new { code = 400, message = error.Message }); }
        catch (InvalidOperationException error) { return StatusCode(409, new { code = 409, message = error.Message }); }
    }

    [HttpDelete]
    [DashboardPermission(DashboardPermission.PluginManagement)]
    public IActionResult Delete([FromQuery] string clusterId, [FromQuery] string destinationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!maintenance.Remove(clusterId, destinationId)) return NotFound();
        audit.RecordSuccess("CancelDestinationMaintenance", clusterId + "/" + destinationId, DashboardPermissionService.Actor(HttpContext));
        return NoContent();
    }
}
