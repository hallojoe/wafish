using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Dashboard.Infrastructure.Diagnostics;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

[ApiController]
[Route("api/diagnostics/bundle")]
[DashboardPermission(DashboardPermission.Read)]
public sealed class DiagnosticBundleController(DiagnosticBundleService diagnostics) : ControllerBase
{
    /// <summary>Downloads redacted diagnostics with bounded collection time and size.</summary>
    [HttpGet]
    public async Task<IActionResult> Download(CancellationToken ct) =>
        File(await diagnostics.CollectAsync(ct), "application/json", "wafish-diagnostics.json");
}
