using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Hosting;

internal sealed class HealthProbePaths(IConfiguration configuration)
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health", "/alive",
        configuration["Gateway:Deployment:HealthCheck:Path"] ?? "/health",
        configuration["Gateway:Deployment:HealthCheck:ReadyPath"] ?? "/ready",
        configuration["Gateway:Deployment:HealthCheck:LivePath"] ?? "/live"
    };

    // Match the probe endpoint itself. A normal API below /health must still produce a trace.
    public bool IsProbe(PathString path) => _paths.Contains(path.Value ?? "");
}
