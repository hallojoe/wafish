using System.Text.Json;
using WafIsh.Dashboard.Infrastructure;
using WafIsh.Dashboard.Modules.GatewayConfig.Services;
using WafIsh.Models;
using WafIsh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace WafIsh.Dashboard.Modules.GatewayConfig.Controllers;

/// <summary>
/// Cluster CRUD operations — save, delete, rename.
/// </summary>
[Route("api/config")]
[ApiController]
public class ClusterConfigController : ConfigControllerBase
{
    private readonly ILogger<ClusterConfigController> _logger;
    private readonly IGatewayIdentityService _identityService;

    public ClusterConfigController(
        IConfigPersistenceService persistenceService,
        IDynamicYarpConfigService dynamicConfig,
        ILogger<ClusterConfigController> logger,
        IMemoryCache memoryCache,
        IGatewayIdentityService identityService,
        IConfigSnapshotScheduler snapshotScheduler,
        IOptionsMonitor<ConfigHistoryOptions> configHistoryOptions)
        : base(persistenceService, dynamicConfig, memoryCache, snapshotScheduler, configHistoryOptions)
    {
        _logger = logger;
        _identityService = identityService;
    }

    /// <summary>
    /// Save or update a single cluster from a full native YARP cluster config.
    /// </summary>
    [HttpPut("clusters/{clusterId}")]
    public async Task<IActionResult> SaveCluster(string clusterId, [FromBody] JsonElement config)
    {
        try
        {
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("Save cluster requested: {ClusterId}", clusterId);
            }

            ClusterConfig? cluster;
            try
            {
                cluster = WafIsh.Serialization.YarpJsonConfig.DeserializeCluster(config);
            }
            catch (Exception parseEx)
            {
                return BadRequest(new { code = 400, message = "Invalid cluster configuration: " + parseEx.Message });
            }

            if (cluster == null)
                return BadRequest(new { code = 400, message = "Cluster configuration is required" });

            cluster = cluster with { ClusterId = clusterId };

            // Strip runtime Health field from destinations to prevent runtime state from being persisted as config
            if (cluster.Destinations != null)
            {
                cluster = cluster with
                {
                    Destinations = cluster.Destinations.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value with { Health = null }
                    )
                };
            }

            if (cluster.Destinations == null || cluster.Destinations.Count == 0)
                return BadRequest(new { code = 400, message = "At least one destination is required" });

            SnapshotScheduler.QueueSnapshot($"After cluster '{clusterId}' saved via dashboard", GetClientIp());

            var result = await DynamicConfig.TryAddClusterConfig(cluster, "dashboard", "dashboard-user");

            if (result.Success) InvalidateQueryCaches();
            return result.Success
                ? Ok(new { code = 200, status = result.Status, activeVersion = result.ActiveVersion, persistedVersion = result.PersistedVersion, message = result.Message, clusterId = clusterId })
                : result.ToHttpResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save cluster: {ClusterId}", clusterId);
            return StatusCode(500, new { code = 500, message = SafeErrorMessages.Create(HttpContext, "Save failed", ex) });
        }
    }

    /// <summary>
    /// Delete a cluster.
    /// </summary>
    [HttpDelete("clusters/{clusterId}")]
    public async Task<IActionResult> DeleteCluster(string clusterId)
    {
        try
        {
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("Delete cluster requested: {ClusterId}", clusterId);
            }

            await PersistenceService.SaveSnapshotAsync($"Before cluster '{clusterId}' deleted via dashboard", GetClientIp());

            var result = await DynamicConfig.TryRemoveCluster(clusterId);

            if (result.Success) InvalidateQueryCaches();
            return result.Success
                ? Ok(new { code = 200, status = result.Status, activeVersion = result.ActiveVersion, persistedVersion = result.PersistedVersion, message = result.Message, clusterId = clusterId })
                : result.ToHttpResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete cluster: {ClusterId}", clusterId);
            return StatusCode(500, new { code = 500, message = SafeErrorMessages.Create(HttpContext, "Delete failed", ex) });
        }
    }

    /// <summary>
    /// Rename a cluster. Updates all referencing routes atomically.
    /// </summary>
    [HttpPut("clusters/{clusterId}/rename")]
    public async Task<IActionResult> RenameCluster(string clusterId, [FromBody] JsonElement config)
    {
        try
        {
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("Rename cluster requested: {OldClusterId}", clusterId);
            }

            string? newClusterId = null;
            if (config.TryGetProperty("newClusterId", out var newIdProp))
                newClusterId = newIdProp.GetString();
            else if (config.TryGetProperty("clusterId", out var newIdProp2))
                newClusterId = newIdProp2.GetString();

            if (string.IsNullOrWhiteSpace(newClusterId))
                return BadRequest(new { code = 400, message = "newClusterId is required" });

            Dictionary<string, string> destinations = new();
            if (config.TryGetProperty("destinations", out var destsProp) || config.TryGetProperty("Destinations", out destsProp))
            {
                if (destsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dest in destsProp.EnumerateObject())
                    {
                        var address = dest.Value.ValueKind == JsonValueKind.String
                            ? dest.Value.GetString() ?? string.Empty
                            : dest.Value.TryGetProperty("address", out var addrProp)
                                ? addrProp.GetString() ?? string.Empty
                                : dest.Value.TryGetProperty("Address", out var addrPascalProp)
                                    ? addrPascalProp.GetString() ?? string.Empty
                                    : string.Empty;
                        if (!string.IsNullOrEmpty(address))
                            destinations[dest.Name] = address;
                    }
                }
            }

            string? loadBalancingPolicy = null;
            if (config.TryGetProperty("loadBalancingPolicy", out var lbpProp))
                loadBalancingPolicy = lbpProp.GetString();

            var result = await _identityService.RenameClusterAsync(
                clusterId, newClusterId, destinations, loadBalancingPolicy,
                clientIp: GetClientIp(), operatorName: "dashboard-user",
                ct: HttpContext.RequestAborted);

            if (result.Success) InvalidateQueryCaches();
            return result.Success
                ? Ok(new { code = 200, status = result.Status, activeVersion = result.ActiveVersion, persistedVersion = result.PersistedVersion, message = result.Message, oldClusterId = clusterId, newClusterId = newClusterId })
                : result.ToHttpResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename cluster: {ClusterId}", clusterId);
            return StatusCode(500, new { code = 500, message = SafeErrorMessages.Create(HttpContext, "Rename failed", ex) });
        }
    }

    /// <summary>
    /// Batch delete clusters in one atomic operation. Clusters referenced by any
    /// route are refused (per-item failure) and left intact.
    /// </summary>
    [HttpPost("clusters/batch-delete")]
    public async Task<IActionResult> BatchDeleteClusters([FromBody] BatchDeleteClustersRequest request)
    {
        if (request is null || request.ClusterIds is null || request.ClusterIds.Count == 0)
            return BadRequest(new { code = 400, message = "clusterIds is required and must be non-empty" });

        try
        {
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("Batch delete {Count} cluster(s) requested", request.ClusterIds.Count);
            }
            await PersistenceService.SaveSnapshotAsync($"Before batch delete {request.ClusterIds.Count} cluster(s) via dashboard", GetClientIp());

            var result = await DynamicConfig.BatchDeleteClustersAsync(request.ClusterIds, "dashboard-user");
            if (result.Succeeded > 0) InvalidateQueryCaches();

            return StatusCode(ConfigurationUpdateHttp.StatusCode(result.Status), new
            {
                code = ConfigurationUpdateHttp.StatusCode(result.Status),
                status = result.Status, activeVersion = result.ActiveVersion, persistedVersion = result.PersistedVersion,
                message = result.Message,
                data = new
                {
                    succeeded = result.Succeeded,
                    failed = result.Failed,
                    total = result.Total,
                    items = result.Items
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to batch delete clusters");
            return StatusCode(500, new { code = 500, message = SafeErrorMessages.Create(HttpContext, "Batch delete failed", ex) });
        }
    }
}

/// <summary>Request body for batch deleting clusters.</summary>
public sealed class BatchDeleteClustersRequest
{
    /// <summary>Cluster ids to delete.</summary>
    public List<string> ClusterIds { get; set; } = new();
}
