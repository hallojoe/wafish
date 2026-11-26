using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Infrastructure.Middleware;
using Wafish.Infrastructure.State;
using Wafish.Plugin.RateLimit;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class WeightedQuotaChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Local quotas: trusted weighted costs share atomic allowances and preserve retry guidance", QuotasAsync);
        await run("Local quotas: invalid costs cannot persist and legacy stores fail closed for weighted requests", ValidationAsync);
        await run("Local quotas: weighted queue cancellation retains and releases partition ownership", CancellationAsync);
    }
    private static RouteConfig Route(string id) => new() { RouteId = id, ClusterId = "backend", Match = new() { Path = "/" + id } };
    private static PluginBindingEntity Binding(string route, string algorithm, int cost) => new()
    {
        Id = "weighted-" + route, PluginId = "rate-limit", PluginVersion = "1.0", Enabled = true, Scope = PluginBindingScope.Route,
        ScopeId = route, ConfigJson = JsonSerializer.Serialize(new { enabled = true, quotaGroup = "shared", algorithm, requestCost = cost,
            permitLimit = 5, tokenLimit = 5, tokensPerPeriod = 1, replenishmentPeriod = "1h", window = "1h", partitionKey = "Global" })
    };
    private static async Task<(Func<string, Task<DefaultHttpContext>> Request, Func<int> Forwarded)> Fixture(IRateLimiterStore store, string algorithm)
    {
        var routes = new[] { Route("cheap"), Route("expensive") };
        var compiler = new GatewaySnapshotCompiler(new InMemoryPluginRepository([Binding("cheap", algorithm, 1), Binding("expensive", algorithm, 3)]));
        var publisher = new GatewaySnapshotPublisher(); publisher.Publish(await compiler.CompileAsync(routes, [], 1));
        var forwarded = 0;
        var middleware = new RateLimitMiddleware(_ => { Interlocked.Increment(ref forwarded); return Task.CompletedTask; }, NullLogger<RateLimitMiddleware>.Instance,
            Options.Create(new GatewayMiddlewareOptions()), null!, store,
            new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
        async Task<DefaultHttpContext> Request(string id)
        {
            var context = new DefaultHttpContext(); context.Request.Path = "/" + id;
            context.Request.Headers["X-Request-Cost"] = "0";
            context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(routes.Single(r => r.RouteId == id), null!, HttpTransformer.Default) });
            await using var body = new MemoryStream(); context.Response.Body = body;
            await middleware.InvokeAsync(context); return context;
        }
        return (Request, () => forwarded);
    }
    private static async Task QuotasAsync()
    {
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket" })
        {
            using var store = new InMemoryRateLimiterStore();
            var fixture = await Fixture(store, algorithm);
            Check((await fixture.Request("expensive")).Response.StatusCode == 200, "Initial weighted request failed.");
            var rejected = await fixture.Request("expensive");
            Check(rejected.Response.StatusCode == 429 && int.Parse(rejected.Response.Headers.RetryAfter.ToString()) > 0, "Weighted rejection lost retry guidance.");
            Check((await fixture.Request("cheap")).Response.StatusCode == 200 && (await fixture.Request("cheap")).Response.StatusCode == 200,
                "Failed acquisition consumed permits or costs used separate quotas.");
            Check((await fixture.Request("cheap")).Response.StatusCode == 429 && fixture.Forwarded() == 3 && store.Count == 1, "Weighted allowance did not remain atomic.");
        }
    }
    private static async Task ValidationAsync()
    {
        var schema = new RateLimitPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
        var validator = new PluginConfigurationSchemaValidator();
        foreach (var (algorithm, cost) in new[] { ("FixedWindow", 0), ("TokenBucket", -1), ("SlidingWindow", 1000001), ("Concurrency", 2) })
            Check(!validator.TryValidate(Binding("cheap", algorithm, cost).ConfigJson, schema, out _, out _), "Invalid cost passed schema validation.");
        Check(validator.TryValidate(Binding("cheap", "Concurrency", 1).ConfigJson, schema, out _, out _), "Default concurrency cost rejected.");
        await using var host = await ConfigurationTransactionChecks.Fixture.Create();
        Check((await host.Config.ImportBatchAsync([new ClusterConfig { ClusterId = "backend", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://127.0.0.1:9/" } } }], [Route("cheap")])).Success, "Fixture setup failed.");
        var mutations = host.App.Services.GetRequiredService<IPluginBindingMutationService>();
        var repository = host.App.Services.GetRequiredService<IPluginConfigurationRepository>();
        var before = await repository.GetBindingsSnapshotAsync(); var version = host.Publisher.Current.Version;
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket", "Concurrency" })
        {
            try { await mutations.UpsertAsync(Binding("cheap", algorithm, 6)); throw new Exception("Oversized cost persisted."); }
            catch (ArgumentException) { }
        }
        var after = await repository.GetBindingsSnapshotAsync();
        Check(after.Revision == before.Revision && after.Bindings.Count == 0 && host.Publisher.Current.Version == version, "Invalid cost changed active or persisted state.");
        using var legacy = new LegacyStore();
        var fixture = await Fixture(legacy, "FixedWindow");
        Check((await fixture.Request("cheap")).Response.StatusCode == 200, "Legacy one-permit store broke.");
        var unavailable = await fixture.Request("expensive");
        Check(unavailable.Response.StatusCode == 503 && unavailable.Response.Headers.RetryAfter == "1" && fixture.Forwarded() == 1, "Legacy store silently undercharged.");
    }
    private static async Task CancellationAsync()
    {
        using var store = new InMemoryRateLimiterStore();
        RateLimiter? limiter = null;
        RateLimiter Factory() => limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromHours(1), AutoReplenishment = false, QueueLimit = 3, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
        using (var acquired = await store.AcquireWeightedAsync("queue", 3, Factory)) Check(acquired?.IsAcquired == true, "Initial cost not acquired.");
        using var cancellation = new CancellationTokenSource();
        var queued = store.AcquireWeightedAsync("queue", 2, Factory, cancellation.Token).AsTask();
        Check(!queued.IsCompleted, "Weighted request was not queued.");
        store.Cleanup(TimeSpan.Zero, 0); Check(store.Count == 1, "Queued partition evicted.");
        var queuedLimiter = limiter!;
        store.Dispose();
        Check(!queued.IsCompleted, "Shutdown disposed an owned queued limiter.");
        cancellation.Cancel();
        try { using var unexpected = await queued; throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        try { using var unexpected = queuedLimiter.AttemptAcquire(); throw new Exception("Cancelled ownership leaked the limiter after shutdown."); } catch (ObjectDisposedException) { }
        try { using var invalid = await store.AcquireWeightedAsync("invalid", 0, Factory); throw new Exception("Zero cost accepted."); } catch (ArgumentOutOfRangeException) { }
    }
    private sealed class LegacyStore : IRateLimiterStore, IDisposable
    {
        private readonly InMemoryRateLimiterStore _store = new();
        public int Count => _store.Count;
        public ValueTask<RateLimitLease?> AcquireAsync(string key, Func<RateLimiter> factory, CancellationToken cancellationToken = default) => _store.AcquireAsync(key, factory, cancellationToken);
        public void Cleanup(TimeSpan staleThreshold, int maxCount) => _store.Cleanup(staleThreshold, maxCount);
        public void Dispose() => _store.Dispose();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
