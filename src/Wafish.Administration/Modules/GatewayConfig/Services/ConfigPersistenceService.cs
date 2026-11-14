using System.Text.Json;
using System.Text.Json.Nodes;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Storage;
using Wafish.Dashboard.Modules.GatewayConfig.Models;
using Wafish.Models;
using Wafish.Services;
using Wafish.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Service for managing configuration snapshots, import/export, and version history using dedicated repositories.
/// </summary>
public class ConfigPersistenceService : IConfigPersistenceService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly DynamicYarpConfigService? _dynamicConfig;
    private readonly IConfigHistoryRepository _historyRepo;
    private readonly IRouteRepository _routeRepo;
    private readonly IClusterRepository _clusterRepo;
    private readonly IOptionsMonitor<ConfigHistoryOptions> _options;
    private readonly ILogger<ConfigPersistenceService> _logger;
    private readonly ConfigSnapshotManager _snapshotManager;

    public ConfigPersistenceService(
        ILogger<ConfigPersistenceService> logger,
        IConfigHistoryRepository historyRepo,
        IRouteRepository routeRepo,
        IClusterRepository clusterRepo,
        IOptionsMonitor<ConfigHistoryOptions> options,
        DynamicYarpConfigService? dynamicConfig = null)
    {
        _historyRepo = historyRepo;
        _routeRepo = routeRepo;
        _clusterRepo = clusterRepo;
        _options = options;
        _logger = logger;
        _dynamicConfig = dynamicConfig;
        _snapshotManager = new ConfigSnapshotManager(
            historyRepo, options, logger, () => ExportFullConfigAsync());
    }

    public async Task<JsonElement> ExportFullConfigAsync()
    {
        var yarpRoutes = _dynamicConfig?.GetRoutes() ?? Array.Empty<RouteConfig>();
        var yarpClusters = _dynamicConfig?.GetClusters() ?? Array.Empty<ClusterConfig>();

        if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
        {
            _logger.LogInformation(
                "ExportFullConfigAsync: dynamicConfig={HasSvc}, routes={RouteCount}, clusters={ClusterCount}",
                _dynamicConfig != null, yarpRoutes.Count, yarpClusters.Count);
        }

        var routesDict = new Dictionary<string, JsonElement>();
        foreach (var r in yarpRoutes)
        {
            var json = Wafish.Serialization.YarpJsonConfig.SerializeRoute(r);
            var obj = JsonNode.Parse(json)?.AsObject();
            if (obj != null) { obj.Remove("RouteId"); obj.Remove("routeId"); }
            var cleanedJson = obj?.ToJsonString() ?? json;
            using var doc = JsonDocument.Parse(cleanedJson);
            routesDict[r.RouteId ?? string.Empty] = doc.RootElement.Clone();
        }

        var clustersDict = new Dictionary<string, JsonElement>();
        foreach (var c in yarpClusters)
        {
            var json = Wafish.Serialization.YarpJsonConfig.SerializeCluster(c);
            var obj = JsonNode.Parse(json)?.AsObject();
            if (obj != null) { obj.Remove("ClusterId"); obj.Remove("clusterId"); }
            var cleanedJson = obj?.ToJsonString() ?? json;
            using var doc = JsonDocument.Parse(cleanedJson);
            clustersDict[c.ClusterId ?? string.Empty] = doc.RootElement.Clone();
        }

        var fullConfig = new Dictionary<string, object>
        {
            ["GatewayState"] = new { RouteActivation = _dynamicConfig?.GetDynamicConfig()?.Routes.ToDictionary(r => r.Config.RouteId, r => r.Enabled) ?? new Dictionary<string, bool>() },
            ["ReverseProxy"] = new Dictionary<string, object>
            {
                ["Routes"] = routesDict,
                ["Clusters"] = clustersDict
            }
        };

        var fullJson = JsonSerializer.Serialize(fullConfig, Wafish.Serialization.YarpJsonConfig.IndentedOptions);
        using var fullDoc = JsonDocument.Parse(fullJson);
        return fullDoc.RootElement.Clone();
    }

    internal static IReadOnlyDictionary<string, bool>? ReadRouteActivation(JsonElement config)
    {
        if (!config.TryGetProperty("GatewayState", out var state) && !config.TryGetProperty("gatewayState", out state))
        {
            return null;
        }

        if (!state.TryGetProperty("RouteActivation", out var activation) && !state.TryGetProperty("routeActivation", out activation))
        {
            return null;
        }

        return activation.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetBoolean(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ImportResult> ImportFullConfigAsync(JsonElement config, string? clientIp = null)
    {
        var result = new ImportResult();
        try
        {
            await SaveSnapshotAsync("Before import", clientIp);

            bool hasReverseProxy = config.TryGetProperty("reverseProxy", out var rpCamel) ||
                                   config.TryGetProperty("ReverseProxy", out rpCamel);
            if (!hasReverseProxy)
            {
                result.Message = "Missing 'ReverseProxy' section";
                return result;
            }
            var reverseProxy = rpCamel;

            if (_dynamicConfig == null)
            {
                result.Message = "Dynamic config service not available";
                return result;
            }

            // Count entries first
            bool hasClusters = reverseProxy.TryGetProperty("clusters", out var cCamel) ||
                               reverseProxy.TryGetProperty("Clusters", out cCamel);
            bool hasRoutes = reverseProxy.TryGetProperty("routes", out var rCamel) ||
                             reverseProxy.TryGetProperty("Routes", out rCamel);

            if (hasClusters)
            {
                foreach (var cluster in cCamel.EnumerateObject())
                {
                    result.TotalClusters++;
                }
            }
            if (hasRoutes)
            {
                foreach (var route in rCamel.EnumerateObject())
                {
                    result.TotalRoutes++;
                }
            }

            // Parse all entries first, then apply in one batch: N items cost one
            // full save + one publish instead of N (30 clusters ≈ 6s → <1s).
            var clustersToImport = new List<ClusterConfig>();
            if (hasClusters)
            {
                foreach (var cluster in cCamel.EnumerateObject())
                {
                    var clusterConfig = Wafish.Serialization.YarpJsonConfig.DeserializeCluster(cluster.Value);
                    if (clusterConfig == null)
                    {
                        result.SkippedClusters++;
                        result.Errors.Add(new ImportItemError
                            { Type = "Cluster", Name = cluster.Name, Error = "Deserialization failed" });
                        continue;
                    }
                    clusterConfig = clusterConfig with { ClusterId = cluster.Name };
                    if (clusterConfig.Destinations == null || clusterConfig.Destinations.Count == 0)
                    {
                        result.SkippedClusters++;
                        result.Errors.Add(new ImportItemError
                            { Type = "Cluster", Name = cluster.Name, Error = "No destinations" });
                        continue;
                    }
                    clustersToImport.Add(clusterConfig);
                }
            }

            var routesToImport = new List<RouteConfig>();
            if (hasRoutes)
            {
                foreach (var route in rCamel.EnumerateObject())
                {
                    var routeConfig = Wafish.Serialization.YarpJsonConfig.DeserializeRoute(route.Value);
                    if (routeConfig == null)
                    {
                        result.SkippedRoutes++;
                        result.Errors.Add(new ImportItemError
                            { Type = "Route", Name = route.Name, Error = "Deserialization failed" });
                        continue;
                    }
                    routeConfig = routeConfig with { RouteId = route.Name };
                    if (string.IsNullOrWhiteSpace(routeConfig.ClusterId))
                    {
                        result.SkippedRoutes++;
                        result.Errors.Add(new ImportItemError
                            { Type = "Route", Name = route.Name, Error = "Missing ClusterId" });
                        continue;
                    }
                    routesToImport.Add(routeConfig);
                }
            }

            var batchResult = await _dynamicConfig.ImportBatchAsync(clustersToImport, routesToImport, "import", "dashboard-user", ReadRouteActivation(config));
            result.Status = batchResult.Status;
            result.ActiveVersion = batchResult.ActiveVersion;
            result.PersistedVersion = batchResult.PersistedVersion;
            if (!batchResult.Success)
            {
                result.Message = batchResult.Message;
                return result;
            }
            result.ImportedClusters = clustersToImport.Count;
            result.ImportedRoutes = routesToImport.Count;

            result.Success = true;
            result.Message = $"Imported {result.ImportedRoutes} routes, {result.ImportedClusters} clusters"
                + (result.SkippedRoutes > 0 || result.SkippedClusters > 0
                    ? $" ({result.SkippedRoutes} routes, {result.SkippedClusters} clusters skipped)"
                    : "");

            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("Configuration imported: {Message}", result.Message);
            }
            await SaveSnapshotAsync("After import", clientIp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import configuration");
            result.Message = $"Import failed: {ex.Message}";
        }
        return result;
    }

    public async Task<ConfigSnapshot> SaveSnapshotAsync(string? description = null, string? clientIp = null)
        => await _snapshotManager.SaveSnapshotAsync(description, clientIp);

    public async Task<IReadOnlyList<ConfigSnapshot>> GetHistoryAsync()
        => await _snapshotManager.GetHistoryAsync();

    public IReadOnlyList<ConfigSnapshot> GetHistory()
        => _snapshotManager.GetHistory();

    public async Task ClearHistoryAsync()
        => await _snapshotManager.ClearHistoryAsync();

    public async Task<bool> RollbackAsync(string versionId, string? clientIp = null)
    {
        ConfigSnapshot? snapshot = await _snapshotManager.FindSnapshotAsync(versionId);

        if (snapshot == null)
        {
            var entity = await _historyRepo.GetConfigHistoryAsync(versionId);
            if (entity != null)
            {
                snapshot = entity.ToConfigSnapshot();
            }
        }

        if (snapshot == null)
        {
            _logger.LogWarning("Snapshot not found: {VersionId}", versionId);
            return false;
        }

        try
        {
            await SaveSnapshotAsync("Before rollback to " + versionId, clientIp);

            var config = snapshot.Config;
            bool hasReverseProxy = config.TryGetProperty("reverseProxy", out var rpC) || config.TryGetProperty("ReverseProxy", out rpC);
            if (!hasReverseProxy)
            {
                _logger.LogWarning(
                    "Rollback aborted: snapshot {VersionId} has no ReverseProxy section, refusing to wipe current config.",
                    versionId);
                return false;
            }

            var yarpRoutes = new List<RouteConfig>();
            var yarpClusters = new List<ClusterConfig>();
            var reverseProxy = rpC;
            if (reverseProxy.TryGetProperty("routes", out var rC) || reverseProxy.TryGetProperty("Routes", out rC))
            {
                foreach (var route in rC.EnumerateObject())
                {
                    var routeConfig = Wafish.Serialization.YarpJsonConfig.DeserializeRoute(route.Value);
                    if (routeConfig == null)
                    {
                        continue;
                    }

                    yarpRoutes.Add(routeConfig with { RouteId = route.Name });
                }
            }

            if (reverseProxy.TryGetProperty("clusters", out var clC) || reverseProxy.TryGetProperty("Clusters", out clC))
            {
                foreach (var cluster in clC.EnumerateObject())
                {
                    var clusterConfig = Wafish.Serialization.YarpJsonConfig.DeserializeCluster(cluster.Value);
                    if (clusterConfig == null)
                    {
                        continue;
                    }

                    yarpClusters.Add(clusterConfig with { ClusterId = cluster.Name });
                }
            }

            // Refuse to apply an empty snapshot: ReplaceAllConfig with empty lists would
            // silently wipe every route/cluster currently in effect.
            if (yarpRoutes.Count == 0 && yarpClusters.Count == 0)
            {
                _logger.LogWarning(
                    "Rollback aborted: snapshot {VersionId} contains no routes or clusters, refusing to wipe current config.",
                    versionId);
                return false;
            }

            if (_dynamicConfig != null)
            {
                await _dynamicConfig.ReplaceAllConfig(yarpRoutes, yarpClusters, "rollback", "dashboard-user", ReadRouteActivation(config));
            }

            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("Rolled back to version: {VersionId}", versionId);
            }
            return true;
        }
        catch (Wafish.Models.ConfigurationUpdateException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rollback to version: {VersionId}", versionId);
            return false;
        }
    }

    public ValidationResult ValidateConfig(JsonElement config)
    {
        var errors = new List<string>();

        if (config.TryGetProperty("ReverseProxy", out var reverseProxy) ||
            config.TryGetProperty("reverseProxy", out reverseProxy))
        {
            if (!(reverseProxy.TryGetProperty("Routes", out _) || reverseProxy.TryGetProperty("routes", out _)))
            {
                errors.Add("Missing 'ReverseProxy.Routes'");
            }

            if (!(reverseProxy.TryGetProperty("Clusters", out _) || reverseProxy.TryGetProperty("clusters", out _)))
            {
                errors.Add("Missing 'ReverseProxy.Clusters'");
            }
        }
        else
        {
            errors.Add("Missing 'ReverseProxy' section");
        }

        return new ValidationResult { Valid = errors.Count == 0, Errors = errors };
    }

    private async Task<GatewayDynamicConfig> LoadConfigFromRepositoryAsync()
    {
        try
        {
            var routeEntities = await _routeRepo.GetAllRoutesAsync();
            var clusterEntities = await _clusterRepo.GetAllClustersAsync();

            var config = new GatewayDynamicConfig { Routes = routeEntities.ToRouteConfigs() };

            foreach (var ce in clusterEntities)
            {
                var dynCluster = ce.ToClusterConfig();
                var destEntities = await _clusterRepo.GetDestinationsAsync(ce.ClusterId);
                dynCluster.Config = dynCluster.Config with
                {
                    Destinations = destEntities.ToDestinations()
                };
                config.Clusters.Add(dynCluster);
            }

            return config;
        }
        catch
        {
            return new GatewayDynamicConfig();
        }
    }
}

/// <summary>
/// Validation result for configuration.
/// </summary>
public class ValidationResult
{
    public bool Valid { get; set; }
    public List<string> Errors { get; set; } = new();
}
