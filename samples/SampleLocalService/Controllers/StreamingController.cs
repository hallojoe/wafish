using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace SampleLocalService.Controllers;

[ApiController]
[Route("/api/local-service/stream")]
public sealed class StreamingController(SampleInstanceIdentity identity, SampleStreamActivity activity, ILogger<StreamingController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Stream([FromQuery] int count = 5, [FromQuery] int intervalMs = 250)
    {
        if (count is < 1 or > 64 || intervalMs is < 0 or > 1000 || (long)(count - 1) * intervalMs > 10000)
            return BadRequest(new { error = "Use 1–64 events, a 0–1000 ms interval, and at most ten seconds total." });
        if (!activity.TryStart()) { Response.Headers.RetryAfter = "1"; return StatusCode(429); }
        var completed = false;
        var streamId = Guid.NewGuid().ToString("N");
        var emitted = 0;
        try
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-store, no-transform";
            for (var sequence = 0; sequence < count; sequence++)
            {
                if (sequence > 0) await Task.Delay(intervalMs, HttpContext.RequestAborted);
                var data = JsonSerializer.Serialize(new { instance = identity.Value, traceId = System.Diagnostics.Activity.Current?.TraceId.ToString(), streamId, sequence });
                await Response.WriteAsync($"id: {sequence}\ndata: {data}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
                emitted++; activity.Emitted();
            }
            completed = true;
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { }
        catch (IOException) { HttpContext.Abort(); }
        finally
        {
            activity.Finish(completed);
            logger.LogInformation("Synthetic stream finished: completed={Completed}, events={Events}", completed, emitted);
        }
        return new EmptyResult();
    }
}
