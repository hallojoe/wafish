using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Models;
using Wafish.Services;

namespace Wafish.Dashboard.Modules.GatewayConfig.Controllers;

/// <summary>Previews and applies routes, clusters, and bindings as one versioned change set.</summary>
[ApiController]
[Route("api/config/change-sets")]
[RequestSizeLimit(4 * 1024 * 1024)]
public sealed class ConfigurationChangeSetsController(IConfigurationChangeSetService changes, IDashboardPermissionService permissions,
    IMemoryCache cache) : ControllerBase
{
    [HttpPost("preview")]
    [DashboardPermission(DashboardPermission.Read)]
    public async Task<IActionResult> Preview(ConfigurationChangeSetRequest request, CancellationToken cancellationToken)
    {
        var result = await changes.PreviewChangeSetAsync(request, DashboardPermissionService.Actor(HttpContext), cancellationToken);
        return StatusCode(result.Valid ? 200 : result.Conflict ? 409 : 400, result);
    }

    [HttpPost]
    [DashboardPermission(DashboardPermission.ConfigurationWrite)]
    [Wafish.Filters.ConfigurationMutation]
    public async Task<IActionResult> Apply(ConfigurationChangeSetRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken cancellationToken)
    {
        if (!await permissions.IsAllowedAsync(HttpContext, DashboardPermission.PluginManagement, "ApplyChangeSet", cancellationToken))
            return StatusCode(403, new { code = 403, message = "PluginManagement permission is required." });
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            return BadRequest(new { code = 400, message = "Supply a printable Idempotency-Key of 1–128 characters." });
        var actor = DashboardPermissionService.Actor(HttpContext);
        var scopedKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor + "\0" + key)));
        var result = await changes.ApplyChangeSetAsync(request, actor, scopedKey, cancellationToken);
        if (result.Configuration.Success)
        {
            cache.Remove("dashboard:routes:query");
            cache.Remove("dashboard:clusters:query");
        }
        var status = result.Configuration.Status switch
        {
            ConfigurationUpdateStatus.Applied => 200,
            ConfigurationUpdateStatus.ValidationRejected => 400,
            ConfigurationUpdateStatus.VersionConflict => 409,
            _ => 500
        };
        return StatusCode(status, result);
    }
}
