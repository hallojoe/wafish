using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class TelemetryConfigurationChecks
{
    private const string Marker = "WAFISH_TELEMETRY_CONFIGURATION ";

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Telemetry view: explicit local opt-in controls startup output without adding HTTP routes", async () =>
        {
            foreach (var scenario in new[]
            {
                (Environment: "Development", Enabled: false, Orchestrated: false, Expected: false),
                (Environment: "Development", Enabled: true, Orchestrated: false, Expected: true),
                (Environment: "Production", Enabled: true, Orchestrated: false, Expected: false),
                (Environment: "Production", Enabled: false, Orchestrated: true, Expected: false),
                (Environment: "Production", Enabled: true, Orchestrated: true, Expected: true)
            })
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = scenario.Environment });
                builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "";
                builder.Configuration["Sample:TelemetryDiagnostics"] = scenario.Enabled.ToString();
                builder.Configuration["Sample:Orchestrated"] = scenario.Orchestrated.ToString();
                builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.AddServiceDefaults();
                await using var app = builder.Build(); app.MapDefaultEndpoints();
                var previous = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    await app.StartAsync();
                    using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
                    using var response = await client.GetAsync("/diagnostics/telemetry");
                    Check(response.StatusCode == System.Net.HttpStatusCode.NotFound, "Telemetry view exposed an HTTP route.");
                    await app.StopAsync();
                }
                finally { Console.SetOut(previous); }
                var lines = output.ToString().Split('\n').Where(line => line.StartsWith(Marker, StringComparison.Ordinal)).ToArray();
                Check(lines.Length == (scenario.Expected ? 1 : 0), "Local opt-in or startup output count is incorrect.");
                if (scenario.Expected)
                {
                    using var report = JsonDocument.Parse(lines.Single()[Marker.Length..]);
                    Check(report.RootElement.GetProperty("exporters").EnumerateObject().All(item => item.Value.GetString() == "none"), "Exporter-free host reported an exporter.");
                }
            }
        });
        await run("Telemetry view: registration snapshot omits secrets, configured names, URLs, and payload settings", async () =>
        {
            foreach (var endpoint in new[] { "", "http://127.0.0.1:4317/private-path?token=secret-token" })
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", ApplicationName = "private-application-name" });
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sample:TelemetryDiagnostics"] = "true",
                    ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
                    ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=secret-header",
                    ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://private-logs.example/path",
                    ["OTEL_RESOURCE_ATTRIBUTES"] = "tenant=private-tenant",
                    ["Gateway:Deployment:HealthCheck:Path"] = "/private-probe",
                    ["SensitivePayload"] = "private-payload"
                });
                builder.ConfigureOpenTelemetry();
                // Later configuration edits must not rewrite the report of earlier registration decisions.
                builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint.Length == 0 ? "http://changed.example" : "";
                var reportService = builder.Services.Where(item => item.ServiceType == typeof(IHostedService))
                    .Select(item => item.ImplementationInstance).OfType<IHostedService>().Single();
                var previous = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    await reportService.StartAsync(CancellationToken.None);
                    await reportService.StopAsync(CancellationToken.None);
                }
                finally { Console.SetOut(previous); }
                var text = output.ToString();
                foreach (var secret in new[] { "private-", "secret-", "http:", "https:", "authorization", "changed.example" })
                    Check(!text.Contains(secret, StringComparison.OrdinalIgnoreCase), "Telemetry report leaked configured data.");
                using var report = JsonDocument.Parse(text.Trim()[Marker.Length..]);
                var root = report.RootElement;
                Check(root.GetProperty("schema").GetInt32() == 1 && root.GetProperty("scope").GetString() == "SampleServiceDefaults" &&
                    root.GetProperty("state").GetString() == "startup-registration", "Report scope or lifecycle is ambiguous.");
                Check(root.GetProperty("exporters").EnumerateObject().All(item => item.Value.GetString() == (endpoint.Length == 0 ? "none" : "otlp")), "Report changed after registration.");
                Check(root.GetProperty("metrics").EnumerateArray().Any(item => item.GetString() == "Wafish.Plugin.Compression") &&
                    root.GetProperty("tracing").EnumerateArray().Any(item => item.GetString() == "HTTP client"), "Report omitted registered instrumentation.");
                Check(root.GetProperty("healthRequestTracing").GetString() == "excluded-exact-configured-paths", "Health filtering is missing.");
            }
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
