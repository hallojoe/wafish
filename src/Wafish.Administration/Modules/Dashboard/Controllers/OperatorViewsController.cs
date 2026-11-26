using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

[ApiController]
[Route("api/operator-views")]
[DashboardPermission(DashboardPermission.Read)]
[RequestSizeLimit(4096)]
public sealed class OperatorViewsController(OperatorViewStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var owner = store.Owner(HttpContext);
        if (owner is null) return Ok(new { code = 200, data = new { available = false, views = Array.Empty<OperatorView>() } });
        try { return Ok(new { code = 200, data = new { available = true, views = await store.ListAsync(owner, cancellationToken) } }); }
        catch (Exception error) when (StorageFailure(error)) { return Unavailable(); }
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var owner = store.Owner(HttpContext); if (owner is null) return StatusCode(403);
        try
        {
            var view = (await store.ListAsync(owner, cancellationToken)).SingleOrDefault(view => view.Id == id);
            return view is null ? NotFound() : Ok(new { code = 200, data = view });
        }
        catch (Exception error) when (StorageFailure(error)) { return Unavailable(); }
    }
    [HttpPost]
    public Task<IActionResult> Create(OperatorViewRequest request, CancellationToken cancellationToken) => Save(null, request, cancellationToken);
    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, OperatorViewRequest request, CancellationToken cancellationToken) => Save(id, request, cancellationToken);
    private async Task<IActionResult> Save(Guid? id, OperatorViewRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var owner = store.Owner(HttpContext); if (owner is null) return StatusCode(403);
        if (!OperatorViewStore.Valid(request)) return BadRequest(new { code = 400, message = "Invalid saved view." });
        try
        {
            var view = await store.SaveAsync(owner, id, request, cancellationToken);
            if (view is null) return NotFound();
            return id is null ? CreatedAtAction(nameof(Get), new { id = view.Id }, new { code = 201, data = view }) : Ok(new { code = 200, data = view });
        }
        catch (OperatorViewConflictException error) { return Conflict(new { code = 409, message = error.Message }); }
        catch (Exception error) when (StorageFailure(error)) { return Unavailable(); }
    }
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var owner = store.Owner(HttpContext); if (owner is null) return StatusCode(403);
        try { return await store.DeleteAsync(owner, id, cancellationToken) ? NoContent() : NotFound(); }
        catch (Exception error) when (StorageFailure(error)) { return Unavailable(); }
    }
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException;
    private ObjectResult Unavailable() => StatusCode(503, new { code = 503, message = "Saved views are unavailable." });
}
