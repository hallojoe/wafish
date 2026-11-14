using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Wafish.AppHost;

internal sealed class SonarQubeHealthCheck(IHttpClientFactory clientFactory, EndpointReference endpoint) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var address = await endpoint.GetValueAsync(cancellationToken);
        using var client = clientFactory.CreateClient("sonarqube-health");
        var status = await client.GetFromJsonAsync<JsonElement>(
            $"{address}/api/system/status", cancellationToken);

        return status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("status", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() == "UP"
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("SonarQube has not completed initialization.");
    }
}
