using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class TelemetryDiagnosticsChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
        => await run("Sample telemetry diagnostics: opt-in local startup configuration excludes exporter secrets and payload data", RunAsync);
    private static async Task RunAsync()
    {
        foreach (var (environment, enabled, exporter) in new[] { ("Development", false, false), ("Production", true, false), ("Development", true, false), ("Development", true, true) })
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sample:TelemetryDiagnostics"] = enabled.ToString(),
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = exporter ? "http://127.0.0.1:9/private-exporter-secret" : "",
                ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=private-header-secret",
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "private-connection-secret",
                ["SyntheticPayload"] = "private-payload-secret"
            });
            builder.AddServiceDefaults();
            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Query.ContainsKey("remote")) context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.4");
                if (context.Request.Query.ContainsKey("missing")) context.Connection.RemoteIpAddress = null;
                await next(context);
            });
            app.MapDefaultEndpoints(); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("/diagnostics/telemetry");
            if (!enabled || environment != "Development") Check(response.StatusCode == HttpStatusCode.NotFound, "Diagnostic view ignored its environment or opt-in boundary.");
            else
            {
                response.EnsureSuccessStatusCode(); var body = await response.Content.ReadAsStringAsync();
                Check(!body.Contains("private-", StringComparison.Ordinal) && !body.Contains("127.0.0.1", StringComparison.Ordinal), "Diagnostic view leaked configuration values.");
                using var json = JsonDocument.Parse(body);
                Check(json.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet().SetEquals(["scope", "otlpExporterEnabled", "metricInstrumentation", "traceInstrumentation", "loggingEnabled", "healthProbeTracesExcluded", "connectivityVerified"]), "Diagnostic view exposed fields outside its whitelist.");
                Check(json.RootElement.GetProperty("scope").GetString() == "SampleServiceDefaults", "Diagnostic scope was misleading.");
                Check(json.RootElement.GetProperty("otlpExporterEnabled").GetBoolean() == exporter && json.RootElement.GetProperty("metricInstrumentation").GetArrayLength() == 6 &&
                    json.RootElement.GetProperty("traceInstrumentation").GetArrayLength() == 4 && json.RootElement.GetProperty("loggingEnabled").GetBoolean() &&
                    json.RootElement.GetProperty("healthProbeTracesExcluded").GetBoolean() && !json.RootElement.GetProperty("connectivityVerified").GetBoolean(), "Diagnostic configuration did not match sample registration.");
                Check(response.Headers.CacheControl?.NoStore == true, "Diagnostics can be cached.");
                builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = exporter ? "" : "http://127.0.0.1:9/changed-secret";
                using var unchanged = JsonDocument.Parse(await client.GetStringAsync("/diagnostics/telemetry"));
                Check(unchanged.RootElement.GetProperty("otlpExporterEnabled").GetBoolean() == exporter, "Reload claimed to change an already registered exporter.");
                foreach (var header in new[] { "Forwarded", "X-Forwarded-For", "X-Original-For" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/diagnostics/telemetry"); request.Headers.Add(header, "192.0.2.4");
                    using var forwarded = await client.SendAsync(request); Check(forwarded.StatusCode == HttpStatusCode.Forbidden, "Forwarded diagnostics were exposed.");
                }
                foreach (var query in new[] { "remote", "missing" })
                { using var remote = await client.GetAsync("/diagnostics/telemetry?" + query); Check(remote.StatusCode == HttpStatusCode.Forbidden, "Non-local diagnostics were exposed."); }
            }
            await app.StopAsync();
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
