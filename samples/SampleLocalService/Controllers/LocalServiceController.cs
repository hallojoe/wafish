using Microsoft.AspNetCore.Mvc;

namespace SampleLocalService.Controllers;

[ApiController]
[Route("/api/local-service/[action]")]
public class LocalServiceController(SampleInstanceIdentity identity) : ControllerBase
{
    [HttpGet]
    public IActionResult Ping()
    {
        return Ok(new
        {
            code = 200,
            service = "SampleLocalService",
            instance = identity.Value,
            traceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    [HttpGet]
    public IActionResult Echo([FromQuery] string? message)
    {
        return Ok(new
        {
            code = 200,
            message = message ?? "hello from local service",
            instance = identity.Value,
            traceId = System.Diagnostics.Activity.Current?.TraceId.ToString()
        });
    }

    [HttpPost]
    public IActionResult EchoPost([FromBody] object? body)
    {
        return Ok(new
        {
            code = 200,
            received = body,
            instance = identity.Value,
            traceId = System.Diagnostics.Activity.Current?.TraceId.ToString()
        });
    }
}
