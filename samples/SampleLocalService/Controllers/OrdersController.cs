using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace SampleLocalService.Controllers;

[ApiController]
[Route("/api/local-service/orders")]
public sealed class OrdersController(SampleInstanceIdentity identity, SampleOrderStore store) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Create([FromBody] SampleOrderRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        var keys = Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !SampleOrderStore.ValidKey(keys[0]) || !SampleOrderStore.Valid(request))
            return BadRequest(new { error = "Supply one 1–64 character ASCII idempotency key and product ID, and a quantity from 1–1000." });
        try
        {
            var result = await store.CreateAsync(keys[0]!, request, HttpContext.RequestAborted);
            var body = Envelope(result.Order);
            return result.Created ? Created($"/api/local-service/orders/{result.Order.Id}", body) : Ok(body);
        }
        catch (SampleOrderConflictException) { return Conflict(new { error = "This key belongs to a different order payload." }); }
        catch (SampleOrderCapacityException exception) { return Unavailable(exception.RetryAfter); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return Unavailable(1); }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var order = await store.GetAsync(id, HttpContext.RequestAborted);
            return order is null ? NotFound() : Ok(Envelope(order));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return Unavailable(1); }
    }

    private object Envelope(SampleOrder order) => new { order, instance = identity.Value, traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() };
    private ObjectResult Unavailable(int retryAfter)
    {
        Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(503, new { error = "Sample order storage is temporarily unavailable." });
    }
}
