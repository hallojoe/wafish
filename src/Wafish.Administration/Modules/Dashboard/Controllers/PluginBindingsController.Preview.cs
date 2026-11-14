using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Models;
using Wafish.Services;
using Wafish.Storage.Entities;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

public sealed partial class PluginBindingsController
{
    /// <summary>Validates a proposed binding without storing or activating it.</summary>
    [HttpPost("preview")]
    [DashboardPermission(DashboardPermission.Read)]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> PreviewBinding(SavePluginBindingRequest request, CancellationToken ct)
    {
        var error = ValidateRequest(request, out var scope, out var json);
        if (error is not null) return error;
        var target = await ResolveTargetAsync(scope, request.ScopeId.Trim(), ct);
        if (target is null) return BadRequest(new { code = 400, message = "Binding target does not exist." });
        var candidate = new PluginBindingEntity
        {
            Id = "preview", PluginId = request.PluginId.Trim(), PluginVersion = _pluginManager.GetManifest(request.PluginId.Trim())!.Version,
            Scope = scope, ScopeId = target.Value.currentId, ConfigJson = json, SchemaVersion = request.SchemaVersion,
            Enabled = request.Enabled, Order = request.Order,
            RouteUid = scope == PluginBindingScope.Route ? target.Value.uid : null,
            ClusterUid = scope == PluginBindingScope.Cluster ? target.Value.uid : null
        };
        var current = _snapshotPublisher.Current;
        await _snapshotCompiler.CompileAsync(current.Routes, current.Clusters, current.Version, ct, [candidate]);
        return Ok(new { code = 200, data = new { candidate.PluginId, candidate.ScopeId, config = JsonSerializer.Deserialize<JsonElement>(json), exclusions = Exclusions(candidate) } });
    }

    private static WafRuleExclusion[] Exclusions(PluginBindingEntity? binding)
    {
        if (binding is null || !binding.PluginId.Equals("Waf", StringComparison.OrdinalIgnoreCase)) return [];
        return JsonSerializer.Deserialize<WafBindingOptions>(binding.ConfigJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Exclusions.ToArray() ?? [];
    }

    private void AuditExclusions(PluginBindingEntity? before, PluginBindingEntity? after)
    {
        var previous = Exclusions(before); var current = Exclusions(after);
        if (previous.Length == 0 && current.Length == 0) return;
        if (previous.SequenceEqual(current) && before?.Enabled == after?.Enabled && before?.ScopeId == after?.ScopeId) return;
        HttpContext?.RequestServices.GetService<IConfigChangeAuditLog>()?.RecordSuccess("WafExclusions", (after ?? before)!.Id,
            DashboardPermissionService.Actor(HttpContext!),
            before: new { before?.ScopeId, before?.Enabled, exclusions = previous },
            after: new { after?.ScopeId, after?.Enabled, exclusions = current });
    }
}
