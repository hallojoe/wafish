using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Storage;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>Authenticated, private preferences. Read permission permits changes to the caller's own views.</summary>
[DashboardPermission(DashboardPermission.Read)]
public sealed class OperatorViewsController(IOperatorViewService service) : ControllerBase
{
    [HttpGet("api/operator-views/{workspace}")]
    public async Task<IActionResult> List(string workspace, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!OperatorViewRules.IsWorkspace(workspace)) return Error(400, "Invalid workspace.");
        var owner = DashboardUserIdentity.Owner(HttpContext);
        if (owner is null || !service.IsAvailable) return Ok(new { code = 200, data = new { available = false, views = Array.Empty<OperatorView>() } });
        return Ok(new { code = 200, data = new { available = true, views = await service.ListAsync(owner, workspace, cancellationToken) } });
    }

    [HttpPut("api/operator-views/{workspace}/{id:guid}")]
    [Consumes("application/json")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Save(string workspace, Guid id, [FromBody] SaveOperatorViewRequest? request, CancellationToken cancellationToken)
    {
        var denied = CheckWrite(out var owner);
        if (denied is not null) return denied;
        if (!ModelState.IsValid || request is null || request.ExpectedRevision < 0 || request.ExpectedRevision == long.MaxValue) return Error(400, "Invalid view or revision.");
        var view = new OperatorView(id, workspace, request.Name, request.Settings, request.IsDefault, request.ExpectedRevision);
        if (!OperatorViewRules.IsValid(view)) return Error(400, "View names, filters, columns, and workspace must meet their documented bounds.");
        return Result(await service.SaveAsync(owner!, view, request.ExpectedRevision, cancellationToken));
    }

    [HttpDelete("api/operator-views/{workspace}/{id:guid}")]
    public async Task<IActionResult> Delete(string workspace, Guid id, [FromQuery] long expectedRevision, CancellationToken cancellationToken)
    {
        var denied = CheckWrite(out var owner);
        if (denied is not null) return denied;
        if (!ModelState.IsValid || !OperatorViewRules.IsWorkspace(workspace) || id == Guid.Empty || expectedRevision < 1) return Error(400, "Invalid view or revision.");
        return Result(await service.DeleteAsync(owner!, workspace, id, expectedRevision, cancellationToken));
    }

    private IActionResult? CheckWrite(out string? owner)
    {
        Response.Headers.CacheControl = "no-store";
        owner = DashboardUserIdentity.Owner(HttpContext);
        if (owner is null) return Error(403, "Saved views require an authenticated user account.");
        if (!service.IsAvailable) return Error(503, "Operator view persistence is unavailable.");
        if (Request.Headers["X-Wafish-Operator-View"].Count != 1 || Request.Headers["X-Wafish-Operator-View"] != "1")
            return Error(403, "An explicit operator-view request is required.");
        if (Request.Headers.TryGetValue("Origin", out var origins) && (origins.Count != 1 ||
            !string.Equals(origins[0], Request.Scheme + "://" + Request.Host, StringComparison.OrdinalIgnoreCase)))
            return Error(403, "Operator-view writes require the same origin.");
        return null;
    }
    private IActionResult Result(OperatorViewWriteResult result) => result.Outcome switch
    {
        OperatorViewWriteOutcome.Saved or OperatorViewWriteOutcome.Deleted => Ok(new { code = 200, success = true, outcome = result.Outcome.ToString(), data = result.View }),
        OperatorViewWriteOutcome.Conflict => Error(409, "The saved view changed. Reload before saving again."),
        OperatorViewWriteOutcome.NameConflict => Error(409, "A saved view already uses this name in this workspace.", "NameConflict"),
        OperatorViewWriteOutcome.NotFound => Error(404, "Saved view not found."),
        _ => Error(409, "Saved view capacity reached. Remove an existing view first.", "Capacity")
    };
    private ObjectResult Error(int status, string message, string? outcome = null) => StatusCode(status, new { code = status, success = false, outcome = outcome ?? (status == 409 ? "Conflict" : "Rejected"), message });
}

/// <summary>Only explicit preferences are accepted. The caller cannot choose an owner or supply arbitrary configuration.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveOperatorViewRequest(string Name, OperatorViewSettings Settings, bool IsDefault, long ExpectedRevision);
