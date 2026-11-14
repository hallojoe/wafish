using Wafish.Models;
using Wafish.Services.ProxyConfigHealth;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>
/// Builds immutable snapshots from the mutable working set and pushes them to
/// <see cref="WafishProxyConfigProvider"/> so YARP can hot-reload.
/// Transforms are normalized for the published (YARP-facing) copy only;
/// the authoritative records are untouched.
/// </summary>
internal class DynamicConfigPublisher : IDynamicConfigPublisher
{
    private readonly WafishProxyConfigProvider _configProvider;
    private readonly IGatewaySnapshotCompiler _snapshotCompiler;
    private readonly IGatewaySnapshotPublisher _snapshotPublisher;
    private readonly IConfigValidator _configValidator;
    private readonly IProxyConfigErrorStore _errorStore;
    private readonly ILogger<DynamicConfigPublisher> _logger;

    public DynamicConfigPublisher(
        WafishProxyConfigProvider configProvider,
        IGatewaySnapshotCompiler snapshotCompiler,
        IGatewaySnapshotPublisher snapshotPublisher,
        IConfigValidator configValidator,
        IProxyConfigErrorStore errorStore,
        ILogger<DynamicConfigPublisher> logger)
    {
        _configProvider = configProvider;
        _snapshotCompiler = snapshotCompiler;
        _snapshotPublisher = snapshotPublisher;
        _configValidator = configValidator;
        _errorStore = errorStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(GatewayDynamicConfig config, long version, CancellationToken cancellationToken = default)
        => (await PrepareAsync(config, version, cancellationToken).ConfigureAwait(false))();

    public Task<Action> PrepareAsync(GatewayDynamicConfig config, long version, CancellationToken cancellationToken = default)
        => PrepareCoreAsync(config, version, true, cancellationToken);

    internal Task<Action> PreparePreviewAsync(GatewayDynamicConfig config, long version, CancellationToken cancellationToken, Action<GatewaySnapshot>? preview = null)
        => PrepareCoreAsync(config, version, false, cancellationToken, preview);

    private async Task<Action> PrepareCoreAsync(GatewayDynamicConfig config, long version, bool recordErrors, CancellationToken cancellationToken, Action<GatewaySnapshot>? preview = null)
    {
        await PreValidateAsync(config.Routes, config.Clusters, recordErrors, cancellationToken).ConfigureAwait(false);
        var publishRoutes = new List<DynamicRouteConfig>(config.Routes.Count);
        foreach (var dynRoute in config.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Skip disabled routes: keep data in working set but don't forward traffic
            if (!dynRoute.Enabled)
            {
                continue;
            }

            var publishedConfig = DynamicYarpConfigHelpers.NormalizeTransforms(dynRoute.Config);
            publishRoutes.Add(new DynamicRouteConfig
            {
                Config = publishedConfig,
                RouteUid = dynRoute.RouteUid,
                ClusterUid = dynRoute.ClusterUid,
                DisplayName = dynRoute.DisplayName,
                Source = dynRoute.Source,
                CreatedAt = dynRoute.CreatedAt,
                CreatedBy = dynRoute.CreatedBy,
                Enabled = true
            });
        }

        var publishClusters = new List<DynamicClusterConfig>(config.Clusters.Count);
        foreach (var dynCluster in config.Clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateCluster = dynCluster.Config;
            if (candidateCluster.Destinations is { } destinations)
                candidateCluster = candidateCluster with { Destinations = destinations.Where(d => !DynamicYarpConfigService.IsDraining(d.Value)).ToDictionary(d => d.Key, d => d.Value) };
            var nativeConfig = SanitizeCluster(candidateCluster);

            // Merge the domain-model health check into the native config when the native side
            // does not already carry one.
            if (nativeConfig.HealthCheck == null && dynCluster.HealthCheck != null)
            {
                var built = DynamicYarpConfigHelpers.BuildClusterHealthCheck(dynCluster.HealthCheck);
                if (built != null)
                {
                    nativeConfig = nativeConfig with { HealthCheck = built };
                }
            }

            publishClusters.Add(new DynamicClusterConfig
            {
                Config = nativeConfig,
                ClusterUid = dynCluster.ClusterUid,
                DisplayName = dynCluster.DisplayName,
                HealthCheck = dynCluster.HealthCheck,
                Source = dynCluster.Source,
                CreatedAt = dynCluster.CreatedAt,
                CreatedBy = dynCluster.CreatedBy,
                LastHeartbeat = dynCluster.LastHeartbeat
            });
        }

        var snapshotVersion = Math.Max(_snapshotPublisher.Current.Version + 1, Math.Max(1, version));
        var gatewaySnapshot = await _snapshotCompiler.CompileAsync(
                publishRoutes.Select(x => x.Config).ToList(),
                publishClusters.Select(x => x.Config).ToList(),
                snapshotVersion, cancellationToken,
                candidateRouteIds: config.Routes.ToDictionary(r => r.RouteUid, r => r.Config.RouteId),
                candidateClusterIds: config.Clusters.ToDictionary(c => c.ClusterUid, c => c.Config.ClusterId)).ConfigureAwait(false);


        gatewaySnapshot = GatewayPublicationCoordinator.For(_snapshotPublisher).PreserveDiscovery(gatewaySnapshot);
        var compiledRoutes = gatewaySnapshot.Routes
            .ToDictionary(x => x.RouteId ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (var route in publishRoutes)
        {
            if (compiledRoutes.TryGetValue(route.Config.RouteId ?? string.Empty, out var compiled))
            {
                route.Config = compiled;
            }
        }

        var compiledClusters = gatewaySnapshot.Clusters
            .ToDictionary(x => x.ClusterId ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in publishClusters)
        {
            if (compiledClusters.TryGetValue(cluster.Config.ClusterId ?? string.Empty, out var compiled))
            {
                cluster.Config = compiled;
            }
        }

        // Reject invalid compiled configuration before storage or publication.
        await PreValidateAsync(publishRoutes, publishClusters, recordErrors, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        preview?.Invoke(gatewaySnapshot);
        return () =>
        {
            _snapshotPublisher.Publish(gatewaySnapshot);
            GatewayPublicationCoordinator.For(_snapshotPublisher).Published(gatewaySnapshot);
            _configProvider.ApplyFromDynamic(publishRoutes, publishClusters, version);
        };
    }

    /// <summary>
    /// Run YARP's <see cref="IConfigValidator"/> over the about-to-be-published routes and
    /// clusters, recording any failures into <see cref="IProxyConfigErrorStore"/> with
    /// <c>Source="prevalidate"</c>. This replaces (not appends to) the previous prevalidate
    /// set so the store reflects the latest cycle only.
    /// </summary>
    private async Task PreValidateAsync(
        IReadOnlyList<DynamicRouteConfig> routes,
        IReadOnlyList<DynamicClusterConfig> clusters, bool recordErrors, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<ProxyConfigError>();
        var now = DateTime.UtcNow;

        foreach (var route in routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IList<Exception> routeErrors;
            try
            {
                routeErrors = await _configValidator.ValidateRouteAsync(route.Config).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Prevalidate ValidateRouteAsync threw for {RouteId}.",
                        route.Config.RouteId);
                }
                throw;
            }

            foreach (var ex in routeErrors)
            {
                errors.Add(new ProxyConfigError
                {
                    RouteId = route.Config.RouteId,
                    Message = ex.Message?.Trim() ?? ex.GetType().Name,
                    ExceptionType = ex.GetType().FullName,
                    OccurredAt = now,
                    Source = "prevalidate"
                });
            }
        }

        foreach (var cluster in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IList<Exception> clusterErrors;
            try
            {
                clusterErrors = await _configValidator.ValidateClusterAsync(cluster.Config).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Prevalidate ValidateClusterAsync threw for {ClusterId}.",
                        cluster.Config.ClusterId);
                }
                throw;
            }

            foreach (var ex in clusterErrors)
            {
                errors.Add(new ProxyConfigError
                {
                    ClusterId = cluster.Config.ClusterId,
                    Message = ex.Message?.Trim() ?? ex.GetType().Name,
                    ExceptionType = ex.GetType().FullName,
                    OccurredAt = now,
                    Source = "prevalidate"
                });
            }
        }

        if (recordErrors) _errorStore.ReplaceErrors("prevalidate", errors);
        var clusterIds = clusters.Select(c => c.Config.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(route.Config.ClusterId) && !clusterIds.Contains(route.Config.ClusterId))
            {
                throw new ArgumentException($"Route '{route.Config.RouteId}' references missing cluster '{route.Config.ClusterId}'.");
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors.Select(e => e.Message)));
        }
    }

    /// <inheritdoc />
    public ClusterConfig SanitizeCluster(ClusterConfig cluster)
    {
        if (cluster.Destinations == null || cluster.Destinations.Count == 0)
        {
            return cluster;
        }

        var validDests = new Dictionary<string, DestinationConfig>(
            cluster.Destinations.Where(d => !string.IsNullOrWhiteSpace(d.Value?.Address)));

        if (validDests.Count == cluster.Destinations.Count)
        {
            return cluster;
        }

        _logger.LogWarning(
            "Dropped {InvalidCount} invalid destinations from cluster {ClusterId}",
            cluster.Destinations.Count - validDests.Count,
            cluster.ClusterId ?? "unknown");

        return cluster with { Destinations = validDests };
    }
}
