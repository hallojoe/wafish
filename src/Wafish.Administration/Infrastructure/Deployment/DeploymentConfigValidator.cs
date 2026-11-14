using Wafish.Dashboard.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wafish.Dashboard.Infrastructure.Deployment;

/// <summary>
/// Hosted service that runs once at startup to validate the deployment configuration.
/// Detects invalid URLs, port conflicts, undefined role references, and public bindings
/// for sensitive roles (Dashboard/Admin). Throws to prevent startup on hard errors;
/// logs warnings for advisory issues.
/// </summary>
public class DeploymentConfigValidator : IHostedService
{
    private readonly IConfiguration _config;
    private readonly ILogger<DeploymentConfigValidator> _logger;

    public DeploymentConfigValidator(IConfiguration config, ILogger<DeploymentConfigValidator> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var kestrelSection = _config.GetSection("Kestrel:Endpoints");
        var endpoints = kestrelSection.GetChildren().ToList();
        var deployment = _config.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>() ?? new DeploymentOptions();
        // 1. URL format
        foreach (var ep in endpoints)
        {
            if (!Uri.TryCreate(ep["Url"], UriKind.Absolute, out _))
            {
                errors.Add($"Invalid URL for endpoint '{ep.Key}': {ep["Url"]}");
            }
        }

        // 2. Port conflicts
        var portMap = new Dictionary<int, string>();
        foreach (var ep in endpoints)
        {
            if (!Uri.TryCreate(ep["Url"], UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Port == 0)
            {
                continue;
            }

            if (portMap.TryGetValue(uri.Port, out var existing))
            {
                errors.Add($"Port {uri.Port} conflict: '{existing}' vs '{ep.Key}'");
            }
            else
            {
                portMap[uri.Port] = ep.Key;
            }
        }

        // 3. Roles reference missing endpoints
        foreach (var kvp in deployment.EndpointRoles)
        {
            if (!endpoints.Any(e => string.Equals(e.Key, kvp.Key, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"EndpointRoles references undefined endpoint: '{kvp.Key}'");
            }
        }

        // 4. Dashboard/Admin roles bind to public addresses
        foreach (var kvp in deployment.EndpointRoles)
        {
            var ep = endpoints.FirstOrDefault(e => string.Equals(e.Key, kvp.Key, StringComparison.OrdinalIgnoreCase));
            if (ep == null || !Uri.TryCreate(ep["Url"], UriKind.Absolute, out var uri))
            {
                continue;
            }

            var isPublic = IsPublicBind(uri.Host);

            if (string.Equals(kvp.Value, "Dashboard", StringComparison.OrdinalIgnoreCase) && isPublic && deployment.RequireLoopbackForDashboard)
            {
                errors.Add($"Dashboard endpoint '{kvp.Key}' is publicly bound ({uri.Host}) but RequireLoopbackForDashboard=true");
            }
            else if (string.Equals(kvp.Value, "Dashboard", StringComparison.OrdinalIgnoreCase) && isPublic)
            {
                warnings.Add($"SECURITY: Dashboard endpoint '{kvp.Key}' is publicly bound to {uri.Host}. Consider binding to 127.0.0.1.");

            }

            if (string.Equals(kvp.Value, "Admin", StringComparison.OrdinalIgnoreCase) && isPublic && deployment.RequireLoopbackForAdmin)
            {
                errors.Add($"Admin endpoint '{kvp.Key}' is publicly bound ({uri.Host}) but RequireLoopbackForAdmin=true");
            }
        }

        // 5. Mode and endpoint configuration do not match
        if (deployment.Mode == DeploymentMode.ProxyOnly &&
            endpoints.Any(e => string.Equals(deployment.EndpointRoles.GetValueOrDefault(e.Key), "Dashboard", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("ProxyOnly mode is set but Dashboard endpoint is configured. It will be ignored.");
        }

        if (deployment.Mode == DeploymentMode.DashboardOnly &&
            endpoints.Any(e => string.Equals(deployment.EndpointRoles.GetValueOrDefault(e.Key), "Proxy", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("DashboardOnly mode is set but Proxy endpoint is configured. It will be ignored.");
        }

        if (!deployment.AutoUseMiddleware)
        {
            warnings.Add("Gateway:Deployment:AutoUseMiddleware=false. Built-in middleware will not be mounted automatically; ensure custom pipeline order mounts required middleware explicitly.");
        }

        // Output
        if (warnings.Count > 0)
        {
            _logger.LogWarning("=== Deployment Configuration Warnings ({Count}) ===", warnings.Count);
            foreach (var w in warnings)
            {
                _logger.LogWarning("{Warning}", w);
            }
        }

        if (errors.Count > 0)
        {
            _logger.LogError("=== Deployment Configuration Errors ({Count}) ===", errors.Count);
            foreach (var e in errors)
            {
                _logger.LogError("{Error}", e);
            }

            throw new InvalidOperationException($"Deployment configuration has {errors.Count} error(s). See logs for details.");
        }

        if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
        {
            _logger.LogInformation("Deployment configuration validated. Mode={Mode}, Endpoints={Count}",
                deployment.Mode, endpoints.Count);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsPublicBind(string host)
        => !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));
}
