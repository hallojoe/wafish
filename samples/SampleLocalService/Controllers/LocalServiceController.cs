using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace SampleLocalService.Controllers;

[ApiController]
[Route("/api/local-service/[action]")]
public class LocalServiceController(SampleInstanceIdentity identity) : ControllerBase
{
    /// <summary>Streams bounded synthetic records and stops when the client disconnects.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SampleStreamResponse), StatusCodes.Status200OK, "application/x-ndjson")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Stream([FromQuery] SampleStreamRequest request,
        [FromServices] ILogger<LocalServiceController> logger, CancellationToken cancellationToken)
    {
        if ((long)(request.Chunks - 1) * request.IntervalMilliseconds > 30000)
            return Problem(statusCode: 400, title: "Scheduled stream duration cannot exceed 30 seconds.");

        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-store, no-transform";
        var written = 0;
        try
        {
            for (var sequence = 1; sequence <= request.Chunks; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = new SampleStreamResponse(sequence, identity.Value,
                    System.Diagnostics.Activity.Current?.TraceId.ToString(), "Synthetic sample record");
                await JsonSerializer.SerializeAsync(Response.Body, record, StreamJson, cancellationToken);
                await Response.WriteAsync("\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                written++;
                if (sequence < request.Chunks)
                    await Task.Delay(request.IntervalMilliseconds, cancellationToken);
            }
            return new EmptyResult();
        }
        finally
        {
            logger.LogInformation(new EventId(5201, "SampleStreamEnded"),
                "Sample stream ended after {Chunks} chunks; cancelled: {Cancelled}", written, cancellationToken.IsCancellationRequested);
        }
    }

    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

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

/// <summary>Bounds the synthetic stream's record count and delay between records.</summary>
public sealed record SampleStreamRequest
{
    [Range(1, 100)] public int Chunks { get; init; } = 5;
    [Range(0, 1000)] public int IntervalMilliseconds { get; init; } = 250;
}

/// <summary>One newline-delimited synthetic stream record.</summary>
public sealed record SampleStreamResponse(int Sequence, string Instance, string? TraceId, string Message);
