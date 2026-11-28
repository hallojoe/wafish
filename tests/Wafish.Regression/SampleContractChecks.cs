using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SampleLocalService;
using SampleLocalService.Controllers;
using Yarp.ReverseProxy.Configuration;

internal static class SampleContractChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Sample contracts: both backend identities, headers, ping, echo, and trace context survive proxying", ContractsAsync);
        await run("Sample telemetry: configured probes are excluded while API traces and expected meters remain active", TelemetryAsync);
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "SampleLocalService", EnvironmentName = "Development" });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "" });
        return builder;
    }

    private static async Task ContractsAsync()
    {
        var apps = new List<WebApplication>();
        var traces = new Traces();
        try
        {
            foreach (var name in new[] { "backend-one", "backend-two" })
            {
                var builder = Builder();
                builder.Configuration["Gateway:Registration:InstanceId"] = name;
                var identity = SampleInstanceIdentity.Resolve(builder.Configuration);
                builder.Services.AddSingleton(identity);
                builder.Services.AddControllers().AddApplicationPart(typeof(LocalServiceController).Assembly);
                builder.AddServiceDefaults();
                builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(traces)));
                var app = builder.Build(); apps.Add(app);
                app.Use(async (context, next) => { identity.Apply(context); await next(context); });
                app.MapControllers();
                await app.StartAsync();
            }
            var gatewayBuilder = Builder(); gatewayBuilder.AddServiceDefaults();
            gatewayBuilder.Services.AddReverseProxy().LoadFromMemory(
                [new RouteConfig { RouteId = "contract", ClusterId = "backends", Match = new() { Path = "/api/local-service/{**rest}" } }],
                [new ClusterConfig { ClusterId = "backends", LoadBalancingPolicy = "RoundRobin", Destinations = apps.Select((app, index) =>
                    KeyValuePair.Create(index == 0 ? "backend-one" : "backend-two", new DestinationConfig { Address = app.Urls.Single() })).ToDictionary() }]);
            var gateway = gatewayBuilder.Build(); apps.Add(gateway); gateway.MapReverseProxy(); await gateway.StartAsync();
            using var client = new HttpClient { BaseAddress = new(gateway.Urls.Single()) };
            using var source = new ActivitySource("SampleLocalService");
            using var activity = source.StartActivity("sample-contract");
            Check(activity is not null, "Application activity source was not registered.");
            foreach (var action in new[] { "Ping", "Echo?message=contract", "Echo", "EchoPost" })
            {
                var identities = new HashSet<string>();
                for (var attempt = 0; attempt < 8 && identities.Count < 2; attempt++)
                {
                    using var response = action == "EchoPost"
                        ? await client.PostAsJsonAsync("/api/local-service/" + action, new { value = 42 })
                        : await client.GetAsync("/api/local-service/" + action);
                    response.EnsureSuccessStatusCode();
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var instance = body.RootElement.GetProperty("instance").GetString()!;
                    Check(response.Headers.GetValues("X-Backend-Instance").Single() == instance, "Body and response header identities differ.");
                    Check(body.RootElement.GetProperty("code").GetInt32() == 200 && body.RootElement.GetProperty("traceId").GetString() == activity!.TraceId.ToString(), "Endpoint contract lost status or trace correlation.");
                    if (action == "EchoPost") Check(body.RootElement.GetProperty("received").GetProperty("value").GetInt32() == 42, "POST echo changed its body.");
                    if (action.StartsWith("Echo") && action != "EchoPost") Check(body.RootElement.GetProperty("message").GetString() == (action.Contains('?') ? "contract" : "hello from local service"), "Query echo changed its message.");
                    identities.Add(instance);
                }
                Check(identities.SetEquals(["backend-one", "backend-two"]), "Both instances were not distinguishable through " + action);
            }
            await WaitAsync(() => traces.Items.Any(item => item.Instance == "backend-one") && traces.Items.Any(item => item.Instance == "backend-two"));
            Check(traces.Items.Where(item => item.Instance is not null).All(item => item.TraceId == activity!.TraceId.ToString()), "Backend trace tags lost parent correlation.");
            var empty = new ConfigurationBuilder().Build();
            Check(SampleInstanceIdentity.Resolve(empty).Value != SampleInstanceIdentity.Resolve(empty).Value, "Standalone instances share their fallback identity.");
        }
        finally { foreach (var app in apps.AsEnumerable().Reverse()) { await app.StopAsync(); await app.DisposeAsync(); } }
    }

    private static async Task TelemetryAsync()
    {
        foreach (var custom in new[] { false, true })
        {
            var builder = Builder();
            var ready = custom ? "/probes/ready" : "/ready";
            var live = custom ? "/probes/live" : "/live";
            var health = custom ? "/probes/health" : "/health";
            builder.Configuration["Gateway:Deployment:HealthCheck:ReadyPath"] = ready;
            builder.Configuration["Gateway:Deployment:HealthCheck:LivePath"] = live;
            builder.Configuration["Gateway:Deployment:HealthCheck:Path"] = health;
            builder.AddServiceDefaults();
            Check(!builder.Services.Any(service => service.ServiceType.FullName?.Contains("Otlp", StringComparison.Ordinal) == true), "Exporter-free defaults registered an OTLP exporter.");
            var traces = new Traces(); var metrics = new Metrics();
            var reader = new BaseExportingMetricReader(metrics);
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(traces)))
                .WithMetrics(meter => meter.AddReader(reader));
            await using var app = builder.Build();
            app.MapDefaultEndpoints();
            foreach (var path in new[] { ready, live, health }.Except(["/health", "/alive"])) app.MapGet(path, () => "ready");
            app.MapGet("/api", () => "api"); app.MapGet("/health/reports", () => "normal API");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            foreach (var path in new[] { "/health", "/alive", ready, live, health, "/api", "/health/reports" }.Distinct())
            { using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode(); await response.Content.ReadAsStringAsync(); }
            await WaitAsync(() => traces.Items.Count(item => item.Kind == ActivityKind.Server) >= 2);
            var servers = traces.Items.Where(item => item.Kind == ActivityKind.Server).ToArray();
            Check(servers.Length == 2 && servers.All(item => item.Route is "/api" or "/health/reports"), "Probe filtering removed an API trace or retained a health trace.");
            Check(traces.Items.Any(item => item.Kind == ActivityKind.Client), "HTTP client tracing is missing.");
            using var yarpSource = new ActivitySource("Yarp.ReverseProxy");
            using (var yarpActivity = yarpSource.StartActivity("source-probe")) Check(yarpActivity is not null, "YARP activity source is missing.");
            using var yarpMeter = new Meter("Yarp.ReverseProxy"); yarpMeter.CreateCounter<long>("sample.registration.probe").Add(1);
            using var compression = new Wafish.Plugin.Compression.CompressionMetricStore();
            compression.Record("sample", "gzip", Wafish.Plugin.Compression.CompressionMetricOutcome.Completed, 100, 20, TimeSpan.FromMilliseconds(1));
            Check(reader.Collect(5000), "Metric collection timed out.");
            foreach (var meter in new[] { "Yarp.ReverseProxy", "Microsoft.AspNetCore.Hosting", "System.Net.Http", "System.Runtime", "Wafish.Plugin.Compression" })
                Check(metrics.Meters.Contains(meter), "Expected telemetry meter is missing: " + meter);
            await app.StopAsync();
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Trace(ActivityKind Kind, string TraceId, string? Route, string? Instance);
    private sealed class Traces : BaseExporter<Activity>
    {
        public ConcurrentQueue<Trace> Items { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Items.Enqueue(new(activity.Kind, activity.TraceId.ToString(),
                activity.GetTagItem("http.route")?.ToString(), activity.GetTagItem("gateway.destination.instance")?.ToString()));
            return ExportResult.Success;
        }
    }
    private sealed class Metrics : BaseExporter<Metric>
    {
        public HashSet<string> Meters { get; } = [];
        public override ExportResult Export(in Batch<Metric> batch)
        { foreach (var metric in batch) Meters.Add(metric.MeterName); return ExportResult.Success; }
    }
}
