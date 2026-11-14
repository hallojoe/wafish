using System.Text.Json;
using WafIsh.Dashboard.Infrastructure.State;
using WafIsh.Dashboard.Modules.GatewayConfig.Models;
using WafIsh.Models;
using WafIsh.Services;
using WafIsh.Storage;
using WafIsh.Storage.Entities;
using Microsoft.Extensions.Logging;
using WafIsh.Infrastructure.State;

namespace WafIsh.Dashboard.Modules.GatewayConfig.Services;

public interface IGatewayIdentityService
{
    Task<RouteOperationResult> RenameClusterAsync(
        string oldClusterId,
        string newClusterId,
        Dictionary<string, string> destinations,
        string? loadBalancingPolicy = null,
        HealthCheckConfig? healthCheck = null,
        string? clientIp = null,
        string? operatorName = "dashboard-user",
        CancellationToken ct = default);

    Task<RouteOperationResult> RenameRouteAsync(
        string oldRouteId,
        string newRouteId,
        RegisterRouteRequest request,
        string? clientIp = null,
        string? operatorName = "dashboard-user",
        CancellationToken ct = default);

    Task AfterClusterRenamedAsync(string oldClusterId, string newClusterId, CancellationToken ct = default);
    Task AfterRouteRenamedAsync(string oldRouteId, string newRouteId, CancellationToken ct = default);
}

/// <summary>
/// Transitional identity service for key rename operations.
/// Long term this becomes the single entry point for UID/key rename operations.
/// </summary>
public sealed class GatewayIdentityService : IGatewayIdentityService
{
    private readonly IPluginConfigurationRepository _pluginConfigurationRepository;
    private readonly IConfigPersistenceService _persistenceService;
    private readonly IDynamicYarpConfigService _dynamicConfig;
    private readonly ICircuitStateStore _circuitStore;
    private readonly ILogger<GatewayIdentityService> _logger;
    private readonly SemaphoreSlim _renameLock = new(1, 1);

    public GatewayIdentityService(
        IPluginConfigurationRepository pluginConfigurationRepository,
        IConfigPersistenceService persistenceService,
        IDynamicYarpConfigService dynamicConfig,
        ICircuitStateStore circuitStore,
        ILogger<GatewayIdentityService> logger)
    {
        _pluginConfigurationRepository = pluginConfigurationRepository;
        _persistenceService = persistenceService;
        _dynamicConfig = dynamicConfig;
        _circuitStore = circuitStore;
        _logger = logger;
    }

    public async Task<RouteOperationResult> RenameClusterAsync(
        string oldClusterId,
        string newClusterId,
        Dictionary<string, string> destinations,
        string? loadBalancingPolicy = null,
        HealthCheckConfig? healthCheck = null,
        string? clientIp = null,
        string? operatorName = "dashboard-user",
        CancellationToken ct = default)
    {
        await _renameLock.WaitAsync(ct);
        ConfigSnapshot? rollbackSnapshot = null;
        try
        {
            rollbackSnapshot = await _persistenceService.SaveSnapshotAsync(
                $"Before cluster '{oldClusterId}' renamed to '{newClusterId}' via dashboard",
                clientIp);

            var result = await _dynamicConfig.TryRenameCluster(
                oldClusterId,
                newClusterId,
                destinations,
                loadBalancingPolicy,
                healthCheck,
                source: "dashboard",
                createdBy: operatorName);

            if (!result.Success) return result;

            await AfterClusterRenamedAsync(oldClusterId, newClusterId, ct);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cluster rename failed: {OldClusterId} -> {NewClusterId}", oldClusterId, newClusterId);
            return new RouteOperationResult(false, "Cluster rename failed. Inspect the configuration before retrying.") { Status = ConfigurationUpdateStatus.StorageFailed };
        }
        finally
        {
            _renameLock.Release();
        }
    }

    public async Task<RouteOperationResult> RenameRouteAsync(
        string oldRouteId,
        string newRouteId,
        RegisterRouteRequest request,
        string? clientIp = null,
        string? operatorName = "dashboard-user",
        CancellationToken ct = default)
    {
        await _renameLock.WaitAsync(ct);
        ConfigSnapshot? rollbackSnapshot = null;
        try
        {
            rollbackSnapshot = await _persistenceService.SaveSnapshotAsync(
                $"Before route '{oldRouteId}' renamed to '{newRouteId}' via dashboard",
                clientIp);

            var result = await _dynamicConfig.TryRenameRoute(
                oldRouteId,
                newRouteId,
                request,
                source: "dashboard",
                createdBy: operatorName);

            if (!result.Success) return result;

            await AfterRouteRenamedAsync(oldRouteId, newRouteId, ct);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Route rename failed: {OldRouteId} -> {NewRouteId}", oldRouteId, newRouteId);
            return new RouteOperationResult(false, "Route rename failed. Inspect the configuration before retrying.") { Status = ConfigurationUpdateStatus.StorageFailed };
        }
        finally
        {
            _renameLock.Release();
        }
    }

    public Task AfterClusterRenamedAsync(string oldClusterId, string newClusterId, CancellationToken ct = default)
    {
        // Binding identities were updated in the configuration transaction.
        _circuitStore.RenameClusterKey(oldClusterId, newClusterId);
        return Task.CompletedTask;
    }

    public Task AfterRouteRenamedAsync(string oldRouteId, string newRouteId, CancellationToken ct = default)
        => Task.CompletedTask;
}
