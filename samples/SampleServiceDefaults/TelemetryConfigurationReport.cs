using System.Text.Json;

namespace Microsoft.Extensions.Hosting;

// Keep the report allowlisted. Never retain IConfiguration, endpoint values, or exporter headers.
internal sealed class TelemetryConfigurationReport(bool otlpEnabled) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var report = new
        {
            schema = 1,
            scope = "SampleServiceDefaults",
            state = "startup-registration",
            exporters = new { logs = otlpEnabled ? "otlp" : "none", metrics = otlpEnabled ? "otlp" : "none", traces = otlpEnabled ? "otlp" : "none" },
            logging = new { includeFormattedMessage = true, includeScopes = true },
            metrics = new[] { "ASP.NET Core", "HTTP client", "Runtime", "Yarp.ReverseProxy", "Wafish.Plugin.Metrics", "Wafish.Plugin.Compression" },
            tracing = new[] { "ASP.NET Core", "HTTP client", "Application activity source", "Yarp.ReverseProxy" },
            healthRequestTracing = "excluded-exact-configured-paths"
        };
        // Console output avoids registering a diagnostic route or exporting the report through OTLP logs.
        Console.WriteLine("WAFISH_TELEMETRY_CONFIGURATION " + JsonSerializer.Serialize(report));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
