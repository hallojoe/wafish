using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Models;
using Wafish.Services;

namespace Wafish.Dashboard.Modules.GatewayConfig.Controllers;

/// <summary>Validates and applies a complete route and cluster configuration.</summary>
[ApiController]
[Route("api/config/changes")]
[RequestSizeLimit(4 * 1024 * 1024)]
public sealed class ConfigurationChangesController(IConfigurationChangeService changes, IMemoryCache cache) : ControllerBase
{
    /// <summary>Returns validation and differences without saving or activating the candidate.</summary>
    [HttpPost("preview")]
    [DashboardPermission(DashboardPermission.Read)]
    public async Task<IActionResult> Preview(ConfigurationChangeRequest request, CancellationToken cancellationToken)
    {
        var preview = await changes.PreviewConfigurationAsync(request, cancellationToken);
        return StatusCode(preview.Valid ? 200 : preview.ExpectedVersion != preview.PersistedVersion ? 409 : 400, preview);
    }

    /// <summary>Applies the candidate once for the authenticated actor and idempotency key.</summary>
    [HttpPost]
    [DashboardPermission(DashboardPermission.ConfigurationWrite)]
    [Wafish.Filters.ConfigurationMutation]
    public async Task<IActionResult> Apply(ConfigurationChangeRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            return new RouteOperationResult(false, "Supply an Idempotency-Key header with 1–128 printable characters.")
                { Status = ConfigurationUpdateStatus.ValidationRejected }.ToHttpResult();
        var scopedKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DashboardPermissionService.Actor(HttpContext) + "\0" + key)));
        var result = await changes.ApplyConfigurationAsync(request, scopedKey, cancellationToken);
        if (result.Success)
        {
            cache.Remove("dashboard:routes:query");
            cache.Remove("dashboard:clusters:query");
        }
        return result.ToHttpResult();
    }
}
