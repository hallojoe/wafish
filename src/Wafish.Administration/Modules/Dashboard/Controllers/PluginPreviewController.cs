using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Dashboard.Infrastructure.Plugin;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>Candidate plugin IDs to inspect without activation.</summary>
public sealed record PluginPreviewRequest(IReadOnlyList<string> EnabledPluginIds);

/// <summary>Reports candidate health and resource requirements without replacing the active domain.</summary>
[ApiController]
[Route("api/plugins/preview")]
[DashboardPermission(DashboardPermission.PluginManagement)]
public sealed class PluginPreviewController(IPluginActivationPreviewService previews) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(128 * 1024)]
    public async Task<IActionResult> Preview(PluginPreviewRequest request, CancellationToken cancellationToken)
    {
        if (request.EnabledPluginIds is null) return BadRequest(new { message = "EnabledPluginIds is required." });
        var result = await previews.PreviewAsync(request.EnabledPluginIds, cancellationToken);
        return StatusCode(result.Ready ? 200 : 400, result);
    }
}
