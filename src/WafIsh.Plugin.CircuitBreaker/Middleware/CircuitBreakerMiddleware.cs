using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WafIsh.Infrastructure.Middleware;
using WafIsh.Plugins;
using WafIsh.Infrastructure.State;
using WafIsh.Infrastructure.Resilience;
using Yarp.ReverseProxy.Model;
using WafIsh.Services;

namespace WafIsh.Plugin.CircuitBreaker;

/// <summary>
/// Per-destination circuit breaker middleware.
/// Reads configuration from cluster-level CircuitBreakerConfig.
/// Tracks rolling failure ratios and owns selected destination probes.
/// States: Closed → Open → HalfOpen → Closed.
/// 
/// Circuit state is managed via <see cref="ICircuitStateStore"/> (Singleton),
/// shared with controllers, warmup services, and retry middleware.
/// </summary>
public sealed class CircuitBreakerMiddleware : GatewayMiddlewareBase
{

    private long _lastCleanup = Environment.TickCount64;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;
    private readonly ICircuitStateStore _circuitStore;
    private readonly IDestinationCandidateCoordinator _destinationCoordinator;


    public CircuitBreakerMiddleware(
        RequestDelegate next,
        ILogger<CircuitBreakerMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions,
        IGatewayPluginManager pluginManager,
        IDynamicYarpConfigService yarpConfig,
        ICircuitStateStore circuitStore,
        GatewayPluginExecutionPlanProvider executionPlans,
        IDestinationCandidateCoordinator destinationCoordinator)
        : base(next, dashOptions, pluginManager, yarpConfig)
    {
        _executionPlans = executionPlans;
        _circuitStore = circuitStore;
        _destinationCoordinator = destinationCoordinator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var feature = context.Features.Get<IReverseProxyFeature>();
        var clusterId = feature?.Cluster.Config.ClusterId;
        if (IsDashboardRequest(context) || clusterId == null ||
            !_executionPlans.Current.CircuitBreakerByCluster.TryGetValue(clusterId, out var config) || !config.Enabled)
        {
            await Next(context);
            return;
        }
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastCleanup);
        if (now - last > 10_800_000 && Interlocked.CompareExchange(ref _lastCleanup, now, last) == last)
        {
            _circuitStore.CleanupStale(TimeSpan.FromHours(3));
        }

        var destination = feature!.AvailableDestinations.Count == 1 ? feature.AvailableDestinations[0] : null;
        using var attempt = destination == null ? null : _circuitStore.TryAcquireAttempt(clusterId, destination.DestinationId, config);
        if (attempt == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        try
        {
            await Next(context);
            if (!context.RequestAborted.IsCancellationRequested)
            {
                attempt.Complete(config.FailureStatusCodes.Contains(context.Response.StatusCode) || context.Response.StatusCode >= 500);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch { attempt.Complete(true); throw; }
        finally { _destinationCoordinator.MarkAttempted(context, feature.ProxiedDestination); }
    }
}
