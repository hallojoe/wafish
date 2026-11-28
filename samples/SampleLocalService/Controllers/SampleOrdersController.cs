using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SampleLocalService.Services;

namespace SampleLocalService.Controllers;

[ApiController]
[Route("/api/local-service/orders")]
public sealed class SampleOrdersController(ISampleOrderService orders) : ControllerBase
{
    /// <summary>Creates a synthetic order or replays the result of the same process-local idempotency key.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<SampleOrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SampleOrderResponse> Create(CreateSampleOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1 || !SampleOrderService.IsValidKey(idempotencyKey))
            return Problem(statusCode: 400, title: "Supply one Idempotency-Key with 16–64 ASCII letters, digits, hyphens, or underscores.");
        var result = orders.Create(idempotencyKey!, request, cancellationToken);
        if (result.Outcome == SampleOrderOutcome.Conflict)
            return Problem(statusCode: 409, title: "This idempotency key already identifies a different synthetic order.");
        if (result.Outcome == SampleOrderOutcome.Capacity)
        {
            Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Problem(statusCode: 503, title: "The sample order store is full. Retry after an existing order expires.");
        }
        Response.Headers["Idempotency-Replayed"] = result.Outcome == SampleOrderOutcome.Replayed ? "true" : "false";
        return Created($"{Request.PathBase}/api/local-service/orders/{result.Order!.Id}", result.Order);
    }

    /// <summary>Reads a synthetic order from this backend until its retention period expires.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<SampleOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public ActionResult<SampleOrderResponse> Get(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var order = orders.Get(id, cancellationToken);
        if (order is null) return Problem(statusCode: 404, title: "The synthetic order is unknown or expired on this backend.");
        return Ok(order);
    }
}
