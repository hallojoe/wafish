using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Wafish.AppHost;

internal sealed class SonarQubeHealthCheck(IHttpClientFactory clientFactory, Func<CancellationToken, Task<string?>> endpoint) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var address = await endpoint(cancellationToken);
            if (string.IsNullOrWhiteSpace(address)) return HealthCheckResult.Unhealthy("SonarQube endpoint is unavailable.");
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
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SonarQube status could not be verified.");
        }
    }
}
