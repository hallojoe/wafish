using Microsoft.AspNetCore.Http;
using Wafish.Infrastructure.State;
using Wafish.Services;
using Yarp.ReverseProxy.Model;

namespace Wafish.Dashboard.Infrastructure.Resilience;

internal sealed class DestinationMaintenanceMiddleware(RequestDelegate next, IDestinationMaintenanceStore maintenance,
    ICircuitStateStore circuits, GatewayPluginExecutionPlanProvider plans)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var feature = context.Features.Get<IReverseProxyFeature>();
        if (feature is not null)
        {
            var clusterId = feature.Cluster.Config.ClusterId;
            var probesEnabled = plans.Current.CircuitBreakerByCluster.TryGetValue(clusterId, out var config) && config.Enabled;
            feature.AvailableDestinations = feature.AvailableDestinations.Where(d => maintenance.AllowsTraffic(clusterId, d.DestinationId,
                probesEnabled && circuits is ICircuitRecoveryState recovery && recovery.IsRecoveryProbeEligible(clusterId, d.DestinationId))).ToArray();
            if (feature.AvailableDestinations.Count == 0) { context.Response.StatusCode = 503; return; }
        }
        await next(context);
    }
}
