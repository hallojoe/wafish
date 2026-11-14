using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Dashboard.Infrastructure.State;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Model;
using Wafish.Infrastructure.State;
using Wafish.Infrastructure.Resilience;
using Wafish.Services;

namespace Wafish.Dashboard.Infrastructure.Resilience;

public sealed class DestinationCandidateCoordinator(
    ICircuitStateStore circuitStateStore,
    GatewayPluginExecutionPlanProvider planProvider) : IDestinationCandidateCoordinator
{
    private readonly IDestinationMaintenanceStore? _maintenance;
    public DestinationCandidateCoordinator(ICircuitStateStore circuits, GatewayPluginExecutionPlanProvider plans, IDestinationMaintenanceStore maintenance)
        : this(circuits, plans) => _maintenance = maintenance;

    private static readonly object InitialCandidatesKey = new();
    private static readonly object AttemptedDestinationsKey = new();

    public ValueTask<IReadOnlyList<DestinationState>> ApplyAsync(
        HttpContext context,
        bool excludeAttempted,
        CancellationToken cancellationToken = default)
    {
        var proxyFeature = context.Features.Get<IReverseProxyFeature>();
        if (proxyFeature is null)
        {
            return ValueTask.FromResult<IReadOnlyList<DestinationState>>(Array.Empty<DestinationState>());
        }

        if (!context.Items.TryGetValue(InitialCandidatesKey, out var initialValue))
        {
            initialValue = proxyFeature.AvailableDestinations.ToArray();
            context.Items[InitialCandidatesKey] = initialValue;
        }

        var initialCandidates = (IReadOnlyList<DestinationState>)initialValue!;
        var attempted = GetAttempted(context);
        var clusterId = proxyFeature.Cluster.Config.ClusterId;
        planProvider.Current.CircuitBreakerByCluster.TryGetValue(clusterId, out var circuitConfig);
        var candidates = new List<DestinationState>(initialCandidates.Count);

        foreach (var destination in initialCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludeAttempted && attempted.Contains(destination.DestinationId))
            {
                continue;
            }

            if (_maintenance is not null && !_maintenance.AllowsTraffic(clusterId, destination.DestinationId,
                circuitConfig is { Enabled: true } && circuitStateStore is ICircuitRecoveryState recovery && recovery.IsRecoveryProbeEligible(clusterId, destination.DestinationId))) continue;
            if (circuitConfig is not { Enabled: true } ||
                !circuitStateStore.IsCircuitOpen(clusterId, destination.DestinationId))
            {
                candidates.Add(destination);
            }
        }

        proxyFeature.AvailableDestinations = candidates;
        proxyFeature.ProxiedDestination = null;
        return ValueTask.FromResult<IReadOnlyList<DestinationState>>(candidates);
    }

    public void MarkAttempted(HttpContext context, DestinationState? destination)
    {
        if (destination is not null)
        {
            GetAttempted(context).Add(destination.DestinationId);
        }
    }

    private static HashSet<string> GetAttempted(HttpContext context)
    {
        if (context.Items.TryGetValue(AttemptedDestinationsKey, out var value))
        {
            return (HashSet<string>)value!;
        }

        var attempted = new HashSet<string>(StringComparer.Ordinal);
        context.Items[AttemptedDestinationsKey] = attempted;
        return attempted;
    }
}
