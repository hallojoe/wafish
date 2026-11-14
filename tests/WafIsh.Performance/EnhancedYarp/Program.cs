using WafIsh.Administration;
using WafIsh.Runtime;
using System.Text.Json;
using WafIsh.Dashboard.Infrastructure;
using WafIsh.Dashboard.Infrastructure.Plugin;
using WafIsh.Extensions;
using WafIsh.Plugins;
using WafIsh.Services;
using WafIsh.Storage.Entities;
using WafIsh.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5302");

var mode = args.FirstOrDefault(x => x.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))?[7..] ?? "full";
if (mode is not ("core" or "storage" or "services" or "host-native" or "minimal" or "full" or "waf" or "waf-attack"
    or "retry" or "circuit" or "rate-fixed" or "rate-sliding" or "rate-token" or "rate-concurrency"
    or "log-meta" or "log-request" or "log-response" or "log-bodies" or "log-sqlite"))
    throw new ArgumentException("Unknown benchmark mode.");
var retryEnabled = mode.StartsWith("retry", StringComparison.OrdinalIgnoreCase);
var circuitEnabled = mode.StartsWith("circuit", StringComparison.OrdinalIgnoreCase);

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Gateway:Storage:Sqlite:ConnectionString"] = $"Data Source=performance-{mode}.db",
    ["Gateway:Deployment:Mode"] = "ProxyOnly",
    ["Gateway:Deployment:HealthCheck:Enabled"] = "false",
    ["Gateway:Dashboard:Auth:AuthMode"] = "None",
    ["Gateway:Security:ControlPlane:AllowAnonymousLoopback"] = "true",
    ["Gateway:ProxyLog:LogPersistenceEnabled"] = (mode == "log-sqlite").ToString(),
    ["Gateway:ProxyLog:EnableProxyRequestBodyCapture"] = (mode is "log-request" or "log-bodies" or "log-sqlite").ToString(),
    ["Gateway:ProxyLog:EnableProxyResponseBodyCapture"] = (mode is "log-response" or "log-bodies" or "log-sqlite").ToString(),
    ["Gateway:ProxyLog:LogMaxBodyLength"] = (256 * 1024).ToString(),
    ["Gateway:ProxyLog:LogMaxBodyBufferBytes"] = (256 * 1024).ToString(),
    ["ReverseProxy:Routes:perf:ClusterId"] = "backend",
    ["ReverseProxy:Routes:perf:Match:Path"] = "/api/perf/{**catch-all}",
    ["ReverseProxy:Clusters:backend:Destinations:backend:Address"] = "http://127.0.0.1:5300"
});

var coreOnly = mode == "core";
var storageOnly = mode == "storage";
var servicesOnly = mode == "services";
var nativeHost = mode == "host-native";

if (nativeHost)
{
    builder.Services.AddReverseProxy().LoadFromMemory(
        [new Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = "perf",
            ClusterId = "backend",
            Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/api/perf/{**catch-all}" }
        }],
        [new Yarp.ReverseProxy.Configuration.ClusterConfig
        {
            ClusterId = "backend",
            Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>
            {
                ["backend"] = new() { Address = "http://127.0.0.1:5300" }
            }
        }]);
}
else
{
    builder.Services.AddWafIsh(enableRegistration: false);
    if (!coreOnly)
    {
        builder.Services.AddWafIshStorage();
        if (!storageOnly) builder.Services.AddWafIshAdministration();
    }
}

var app = builder.Build();
app.UseRouting();
if (coreOnly || storageOnly || servicesOnly || nativeHost)
{
    app.MapReverseProxy();
}
else
{
    var useFullPipeline = mode != "minimal";
    app.UseWafIshGateway(new GatewayPipelineOptions
    {
        AutoUseMiddleware = useFullPipeline,
        UseDeploymentMiddleware = false,
        UseBuiltInProxyPipeline = useFullPipeline,
        AutoUseCors = false,
        AutoUseAuthorization = false
    });
}
var ready = false;
string[] effectivePolicies = [];
app.MapGet("/health", () => Volatile.Read(ref ready) ? Results.Ok(new { mode, effectivePolicies }) : Results.StatusCode(503));

await app.StartAsync();
if (!coreOnly && !storageOnly && !servicesOnly && !nativeHost && mode != "minimal")
{
    var configService = app.Services.GetRequiredService<IDynamicYarpConfigService>();
    var mutations = app.Services.GetRequiredService<IPluginBindingMutationService>();
    var manifests = app.Services.GetRequiredService<IGatewayPluginManager>();
    var dynamicConfig = configService.GetDynamicConfig();
    async Task BindRouteAsync(string pluginId, object settings)
    {
        var route = dynamicConfig!.Routes.First(item => item.Config.RouteId == "perf");
        var manifest = manifests.GetManifest(pluginId)!;
        var schema = manifest.Schemas.First();
        var json = JsonSerializer.Serialize(settings);
        var validator = app.Services.GetRequiredService<IPluginConfigurationSchemaValidator>();
        if (!validator.TryValidate(json, schema.ConfigJsonSchema, out json, out var error))
            throw new InvalidOperationException(error);
        await mutations.UpsertAsync(new PluginBindingEntity
        {
            Id = $"route:{route.RouteUid}:{pluginId}", PluginId = pluginId,
            PluginVersion = manifest.Version, Scope = PluginBindingScope.Route,
            ScopeId = "perf", RouteUid = route.RouteUid, ConfigJson = json, SchemaVersion = schema.Version
        });
        var active = app.Services.GetRequiredService<IGatewaySnapshotPublisher>().Current;
        if (!active.RoutePlugins.TryGetValue("perf", out var plugins) || !plugins.Any(p => p.PluginId == pluginId))
            throw new InvalidOperationException($"Benchmark binding {pluginId} did not activate.");
    }
    if (mode is "waf" or "waf-attack") await BindRouteAsync("waf", new { enabled = true });
    if (mode.StartsWith("rate-", StringComparison.Ordinal))
    {
        var algorithm = mode switch
        {
            "rate-fixed" => "FixedWindow", "rate-sliding" => "SlidingWindow",
            "rate-token" => "TokenBucket", "rate-concurrency" => "Concurrency",
            _ => throw new ArgumentException("Unknown rate benchmark mode.")
        };
        await BindRouteAsync("rate-limit", new { enabled = true, algorithm, permitLimit = 100,
            window = "1s", queueLimit = 0, partitionKey = "Global", tokenLimit = 100, tokensPerPeriod = 100 });
    }
    if (mode.StartsWith("log-", StringComparison.Ordinal))
        await BindRouteAsync("proxy-log", new {
            requestBodyCaptureEnabled = mode is "log-request" or "log-bodies" or "log-sqlite",
            responseBodyCaptureEnabled = mode is "log-response" or "log-bodies" or "log-sqlite",
            maxBodyLength = 262144, maxBodyBufferBytes = 262144 });
    if (retryEnabled)
    {
        var route = dynamicConfig?.Routes.First(item => item.Config.RouteId == "perf")
            ?? throw new InvalidOperationException("Performance route is unavailable.");
        var manifest = manifests.GetManifest("request-retry")
            ?? throw new InvalidOperationException("Retry plugin manifest is unavailable.");
        await mutations.UpsertAsync(new PluginBindingEntity
        {
            Id = $"route:{route.RouteUid}:request-retry",
            PluginId = "request-retry",
            PluginVersion = manifest.Version,
            Scope = PluginBindingScope.Route,
            ScopeId = "perf",
            RouteUid = route.RouteUid,
            ConfigJson = JsonSerializer.Serialize(new
            {
                enabled = true,
                maxRetries = 2,
                backoffBaseMs = 1,
                backoffJitterMs = 1,
                retryOnStatusCodes = new[] { 502, 503, 504 }
            }),
            SchemaVersion = manifest.Schemas.FirstOrDefault()?.Version ?? 1
        });
    }
    if (circuitEnabled)
    {
        var cluster = dynamicConfig?.Clusters.First(item => item.Config.ClusterId == "backend")
            ?? throw new InvalidOperationException("Performance cluster is unavailable.");
        var manifest = manifests.GetManifest("circuit-breaker")
            ?? throw new InvalidOperationException("Circuit breaker plugin manifest is unavailable.");
        await mutations.UpsertAsync(new PluginBindingEntity
        {
            Id = $"cluster:{cluster.ClusterUid}:circuit-breaker",
            PluginId = "circuit-breaker",
            PluginVersion = manifest.Version,
            Scope = PluginBindingScope.Cluster,
            ScopeId = "backend",
            ClusterUid = cluster.ClusterUid,
            ConfigJson = JsonSerializer.Serialize(new
            {
                enabled = true,
                failureThreshold = 3,
                recoveryTimeoutSeconds = 2,
                halfOpenMaxAttempts = 1,
                failureRatio = 0.5,
                minimumThroughput = 3,
                samplingDurationSeconds = 30,
                failureStatusCodes = new[] { 500, 502, 503, 504 }
            }),
            SchemaVersion = manifest.Schemas.FirstOrDefault()?.Version ?? 1
        });
    }
}
if (!coreOnly && !storageOnly && !servicesOnly && !nativeHost && mode != "minimal")
{
    var snapshot = app.Services.GetRequiredService<IGatewaySnapshotPublisher>().Current;
    var manifests = app.Services.GetRequiredService<IGatewayPluginManager>();
    var validator = app.Services.GetRequiredService<IPluginConfigurationSchemaValidator>();
    foreach (var binding in snapshot.RoutePlugins.Values.SelectMany(x => x).Concat(snapshot.ClusterPlugins.Values.SelectMany(x => x)))
    {
        var schema = manifests.GetManifest(binding.PluginId)!.Schemas.Single(s => s.Version == binding.SchemaVersion);
        if (!validator.TryValidate(binding.ConfigJson, schema.ConfigJsonSchema, out _, out var error))
            throw new InvalidOperationException(error);
    }
    var plans = app.Services.GetRequiredService<GatewayPluginExecutionPlanProvider>().Current;
    effectivePolicies = snapshot.RoutePlugins.Values.SelectMany(x => x).Concat(snapshot.ClusterPlugins.Values.SelectMany(x => x))
        .Select(binding => binding.PluginId).Distinct().Order().ToArray();
    if (mode is "waf" or "waf-attack" && (!plans.WafByRoute.TryGetValue("perf", out var waf) || !waf.Enabled))
        throw new InvalidOperationException("WAF benchmark policy is inactive.");
    if (mode.StartsWith("rate-", StringComparison.Ordinal))
    {
        var expected = mode switch { "rate-fixed" => "FixedWindow", "rate-sliding" => "SlidingWindow", "rate-token" => "TokenBucket", _ => "Concurrency" };
        if (!plans.RateLimitByRoute.TryGetValue("perf", out var rate) || !rate.Enabled || rate.Algorithm.ToString() != expected)
            throw new InvalidOperationException("Rate-limit benchmark algorithm differs from its mode.");
    }
    if (mode.StartsWith("log-", StringComparison.Ordinal))
    {
        if (!plans.ProxyLogByRoute.TryGetValue("perf", out var log) ||
            log.RequestBodyCaptureEnabled != (mode is "log-request" or "log-bodies" or "log-sqlite") ||
            log.ResponseBodyCaptureEnabled != (mode is "log-response" or "log-bodies" or "log-sqlite"))
            throw new InvalidOperationException("Logging benchmark capture differs from its mode.");
    }
    if (retryEnabled && !plans.RetryByRoute.ContainsKey("perf") || circuitEnabled && !plans.CircuitBreakerByCluster.ContainsKey("backend"))
        throw new InvalidOperationException("Requested resilience policy is inactive.");
}
Volatile.Write(ref ready, true);
await app.WaitForShutdownAsync();
