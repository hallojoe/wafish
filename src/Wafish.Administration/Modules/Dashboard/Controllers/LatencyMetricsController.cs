using Microsoft.AspNetCore.Mvc;
using Wafish.Plugin.Metrics;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

[ApiController]
[Route("api/metrics/latency")]
public sealed class LatencyMetricsController(PluginMetricStore metrics) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { code = 200, data = new { unit = "s", routes = metrics.GetRoutes(), clusters = metrics.GetClusters() } });
}
