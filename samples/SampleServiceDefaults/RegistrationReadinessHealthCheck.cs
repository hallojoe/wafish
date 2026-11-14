using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wafish.Services;

namespace Microsoft.Extensions.Hosting;

/// <summary>Optional readiness check. It never performs a network operation.</summary>
public sealed class RegistrationReadinessHealthCheck(IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = services.GetService<IRegistrationStatusSource>()?.GetRegistrationStatus();
        return Task.FromResult(status?.Ready == true ? HealthCheckResult.Healthy("Gateway registration is ready.")
            : HealthCheckResult.Unhealthy("Gateway registration is not ready."));
    }
}
