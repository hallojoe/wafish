using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Plugin;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>Reports domain generations and requests delaying plugin shutdown.</summary>
[ApiController]
[Route("api/plugins/drain")]
public sealed class PluginDiagnosticsController(IPluginDrainDiagnostics diagnostics) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { code = 200, data = diagnostics.GetDrainSnapshot() });
}
