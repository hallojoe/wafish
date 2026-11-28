using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.Infrastructure.Middleware;
using Wafish.Infrastructure.State;
using Wafish.Models;
using Wafish.Plugin.RateLimit;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class WeightedQuotaChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static RateLimitExecutionConfig Policy() => new() { Enabled = true, PermitLimit = 5, TokenLimit = 5, TokensPerPeriod = 1,
        Window = "1h", ReplenishmentPeriod = "1h", PartitionKey = "Global", RequestCost = 3 };
    private static string Config(RateLimitExecutionConfig config) => JsonSerializer.Serialize(new {
        config.Enabled, config.Algorithm, config.PermitLimit, config.TokenLimit, config.TokensPerPeriod,
        config.Window, config.ReplenishmentPeriod, config.PartitionKey, config.RequestCost, config.QuotaGroup, config.QueueLimit, config.SegmentsPerWindow
    }, Json);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid weighted configuration accepted."); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Weighted quotas: costs validate against algorithm capacity and preserve quota identity", () =>
        {
            Check(LocalRequestCostPolicy.Compile(new()).RequestCost == 1, "Default request cost changed.");
            foreach (var invalid in new[] { Policy() with { RequestCost = 0 }, Policy() with { RequestCost = -1 }, Policy() with { RequestCost = 1000001 },
                Policy() with { RequestCost = 6 }, Policy() with { Algorithm = RateLimitAlgorithm.TokenBucket, TokenLimit = 2 },
                Policy() with { Algorithm = RateLimitAlgorithm.Concurrency }, Policy() with { Algorithm = (RateLimitAlgorithm)100 } })
                Reject(() => LocalRequestCostPolicy.Compile(invalid));
            foreach (var algorithm in new[] { RateLimitAlgorithm.FixedWindow, RateLimitAlgorithm.SlidingWindow, RateLimitAlgorithm.TokenBucket })
            {
                var first = LocalQuotaGroupPolicy.Compile(Policy() with { Algorithm = algorithm, QuotaGroup = "shared" });
                var second = LocalQuotaGroupPolicy.Compile(first with { RequestCost = 1 });
                Check(first.QuotaGroupPolicy == second.QuotaGroupPolicy, "Route cost changed shared capacity identity.");
            }
            LocalRequestCostPolicy.Compile(Policy() with { Algorithm = RateLimitAlgorithm.Concurrency, RequestCost = 1 });
            using var store = new InMemoryRateLimiterStore(); var compiler = new RateLimitGroupCompiler(store);
            CompiledRoutePlugin Compile(string id, RateLimitExecutionConfig policy, int schema = 1) => compiler.Compile(
                new("binding-" + id, "rate-limit", PluginBindingScope.Route, id, Config(policy), schema, 1, 0), new() { RouteId = id });
            var plans = new[] { Compile("cheap", Policy() with { QuotaGroup = "shared", RequestCost = 1 }), Compile("expensive", Policy() with { QuotaGroup = "shared" }) };
            compiler.Validate(GatewaySnapshot.Empty with { RouteExecutionPlans = plans.ToImmutableDictionary(p => p.Route.RouteId, p => new RouteExecutionPlan(p.Route.RouteId, [p])) });
            Check(store.Count == 0, "Preview allocated weighted quota state.");
            Reject(() => Compile("bad", Policy(), 2));
            using var legacy = new LegacyStore();
            Reject(() => new RateLimitGroupCompiler(legacy).Compile(plans[1].Binding, plans[1].Route));
            Reject(() => new RateLimitGroupCompiler(legacy).Compile(Compile("single", Policy()).Binding, new() { RouteId = "single" }));
            var schemaText = new RateLimitPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
            var validator = new PluginConfigurationSchemaValidator();
            Check(validator.TryValidate(Config(Policy()), schemaText, out _, out var error), error);
            Check(!validator.TryValidate("{\"requestCost\":1.5}", schemaText, out _, out _), "Fractional cost accepted.");
            return Task.CompletedTask;
        });
        await run("Weighted quotas: atomic costs, shared routes, cost changes, and retry metadata preserve remaining permits", async () =>
        {
            foreach (var algorithm in new[] { RateLimitAlgorithm.FixedWindow, RateLimitAlgorithm.SlidingWindow, RateLimitAlgorithm.TokenBucket })
            foreach (var grouped in new[] { false, true })
            {
                using var store = new InMemoryRateLimiterStore(); var harness = new Harness(store);
                var expensive = Policy() with { Algorithm = algorithm, QuotaGroup = grouped ? "shared" : null };
                var cheap = expensive with { RequestCost = 1 };
                harness.Publish(("expensive", expensive), ("cheap", cheap));
                Check((await harness.Request("expensive")).Status == 204, "Initial weighted request failed.");
                var rejected = await harness.Request("expensive");
                Check(rejected.Status == 429 && rejected.Retry >= 3600 && rejected.Body.Contains("retryAfter"), "Weighted rejection lost useful retry guidance.");
                if (!grouped) harness.Publish(("expensive", cheap));
                var remainderRoute = grouped ? "cheap" : "expensive";
                Check((await harness.Request(remainderRoute)).Status == 204 && (await harness.Request(remainderRoute)).Status == 204,
                    "Rejection partially charged permits or cost changes reset counters.");
                Check((await harness.Request(remainderRoute)).Status == 429 && store.Count == 1, "Weighted allowance was exceeded.");
            }
            using var legacy = new LegacyStore(); var old = new Harness(legacy);
            old.Publish(("unit", Policy() with { RequestCost = 1 }), ("weighted", Policy()), ("grouped", Policy() with { QuotaGroup = "group" }));
            Check((await old.Request("unit")).Status == 204, "Existing unit-cost store stopped working.");
            Check((await old.Request("weighted")).Status == 503 && (await old.Request("grouped")).Status == 503,
                "Unsupported stores silently charged one permit for weighted requests.");
        });
        await run("Weighted quotas: window queues cancel without charging and token retry values retain large costs", async () =>
        {
            foreach (var algorithm in new[] { RateLimitAlgorithm.FixedWindow, RateLimitAlgorithm.SlidingWindow, RateLimitAlgorithm.TokenBucket })
            foreach (var grouped in new[] { false, true })
            {
                using var store = new InMemoryRateLimiterStore(); var harness = new Harness(store);
                var policy = Policy() with { Algorithm = algorithm, QueueLimit = 3, QuotaGroup = grouped ? "queued" : null };
                harness.Publish(("queued", policy));
                Check((await harness.Request("queued")).Status == 204, "Queue setup failed.");
                using var cancellation = new CancellationTokenSource();
                var queued = harness.Request("queued", cancellation.Token);
                Check(!queued.IsCompleted, "Native window or token limiter did not queue the whole cost.");
                cancellation.Cancel();
                try { await queued; throw new Exception("Weighted middleware ignored cancellation."); } catch (OperationCanceledException) { }
                harness.Publish(("queued", policy with { RequestCost = 1 }));
                Check((await harness.Request("queued")).Status == 204 && (await harness.Request("queued")).Status == 204,
                    "Cancelled weighted acquisition charged partial permits.");
                using var smallQueueStore = new InMemoryRateLimiterStore(); var smallQueue = new Harness(smallQueueStore);
                smallQueue.Publish(("small", policy with { QueueLimit = 2 }));
                Check((await smallQueue.Request("small")).Status == 204 && (await smallQueue.Request("small")).Status == 429,
                    "Queue limit counted requests instead of weighted permits.");
            }
            using var longWaitStore = new InMemoryRateLimiterStore(); var longWait = new Harness(longWaitStore);
            longWait.Publish(("large", Policy() with { Algorithm = RateLimitAlgorithm.TokenBucket, RequestCost = 1000000, TokenLimit = 1000000 }));
            Check((await longWait.Request("large")).Status == 204, "Large validated token cost failed.");
            var retry = await longWait.Request("large");
            Check(retry.Status == 429 && retry.Retry == 3600000000L && retry.Body.Contains("3600000000"), "Large weighted retry overflowed or lost its JSON value.");
        });
        await run("Weighted quotas: queued cancellation, acquisition failures, and disposal retain partition ownership", async () =>
        {
            foreach (var grouped in new[] { false, true })
            {
                using var store = new InMemoryRateLimiterStore();
                using var limiter = new ConcurrencyLimiter(new() { PermitLimit = 3, QueueLimit = 3, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
                ValueTask<RateLimitLease?> Acquire(int count, CancellationToken cancellation = default) => grouped
                    ? store.AcquireWeightedGroupedAsync("group", "policy", "partition", count, () => limiter, cancellation)
                    : store.AcquireWeightedAsync("partition", count, () => limiter, cancellation);
                foreach (var count in new[] { 0, -1 })
                {
                    try { using var invalid = await Acquire(count); throw new Exception("Invalid cost reached the limiter."); } catch (ArgumentOutOfRangeException) { }
                }
                Check(store.Count == 0, "Invalid costs allocated a partition.");
                var held = await Acquire(2); Check(held!.IsAcquired, "Weighted ownership setup failed.");
                using var cancellation = new CancellationTokenSource();
                var queued = Acquire(2, cancellation.Token).AsTask();
                Check(!queued.IsCompleted, "Weighted request ignored queue capacity.");
                store.Cleanup(TimeSpan.Zero, 0); Check(store.Count == 1, "Queued or active weighted ownership was evicted.");
                cancellation.Cancel();
                try { using var cancelled = await queued; throw new Exception("Weighted queue ignored cancellation."); } catch (OperationCanceledException) { }
                held.Dispose(); held.Dispose();
                using (var full = await Acquire(3)) Check(full!.IsAcquired, "Cancellation or duplicate disposal leaked permits.");
                try { using var tooLarge = await Acquire(4); throw new Exception("Oversized store acquisition succeeded."); } catch (ArgumentOutOfRangeException) { }
                using (var recovered = await Acquire(3)) Check(recovered!.IsAcquired, "Failed acquisition retained an owner.");
                using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
                try { using var cancelled = await Acquire(1, alreadyCancelled.Token); throw new Exception("Pre-cancelled acquisition succeeded."); } catch (OperationCanceledException) { }
                var outstanding = await Acquire(3); store.Dispose(); outstanding!.Dispose();
            }
        });
        await run("Weighted quotas: real bindings reject invalid costs and enforce mixed-cost routes", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start();
            var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            var repository = fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var publisher = fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            foreach (var id in new[] { "cheap", "expensive" })
            {
                Check((await routes.TryAddRouteConfig(new() { RouteId = id, Match = new() { Path = "/" + id } })).Success, "Route setup failed.");
                await fixture.Bind(id, "redirect", "{\"target\":\"/allowed\"}"); await fixture.WaitRoute(id);
            }
            var before = publisher.Current;
            foreach (var enabled in new[] { true, false })
            foreach (var invalid in new[] { Policy() with { RequestCost = 0 }, Policy() with { RequestCost = 6 }, Policy() with { Algorithm = RateLimitAlgorithm.Concurrency } })
            {
                try { await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(new()
                { PluginId = "rate-limit", PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "cheap", Enabled = enabled, ConfigJson = Config(invalid) }); throw new Exception("Invalid weighted binding persisted."); }
                catch (ArgumentException) { }
                Check(!(await repository.GetBindingsAsync()).Any(b => b.PluginId == "rate-limit") && ReferenceEquals(before, publisher.Current), "Rejected cost changed configuration.");
            }
            await fixture.Bind("cheap", "rate-limit", Config(Policy() with { QuotaGroup = "shared", RequestCost = 1 }));
            await fixture.Bind("expensive", "rate-limit", Config(Policy() with { QuotaGroup = "shared" }));
            foreach (var (route, status) in new[] { ("cheap", 307), ("expensive", 307), ("expensive", 429), ("cheap", 307), ("cheap", 429) })
            {
                using var response = await fixture.Client.GetAsync("/" + route); Check((int)response.StatusCode == status, "Real mixed-cost route returned " + response.StatusCode);
                if (status == 429) Check(response.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1), "Real weighted rejection omitted retry timing.");
            }
        });
    }

    private sealed class Harness(IRateLimiterStore store)
    {
        private readonly GatewaySnapshotPublisher _publisher = new();
        private long _version; private Dictionary<string, RouteConfig> _routes = [];
        internal void Publish(params (string Id, RateLimitExecutionConfig Config)[] routes)
        {
            _routes = routes.ToDictionary(p => p.Id, p => new RouteConfig { RouteId = p.Id, Match = new() { Path = "/" + p.Id } });
            _publisher.Publish(GatewaySnapshot.Empty with { Version = ++_version, Routes = _routes.Values.ToImmutableArray(),
                RoutePlugins = routes.ToImmutableDictionary(p => p.Id, p => ImmutableArray.Create(new PluginBindingSnapshot("binding-" + p.Id, "rate-limit", PluginBindingScope.Route, p.Id, Config(p.Config), 1, 1, 0))) });
        }
        internal async Task<(int Status, long Retry, string Body)> Request(string route, CancellationToken cancellation = default)
        {
            var context = new DefaultHttpContext { RequestAborted = cancellation }; context.Request.Path = "/" + route;
            context.Request.Headers["X-Request-Cost"] = "0"; context.Request.QueryString = new("?requestCost=0");
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(_routes[route], null!, HttpTransformer.Default) });
            await using var body = new MemoryStream(); context.Response.Body = body; var forwarded = false;
            var middleware = new RateLimitMiddleware(c => { forwarded = true; c.Response.StatusCode = 204; return Task.CompletedTask; },
                NullLogger<RateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store,
                new(_publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
            await middleware.InvokeAsync(context); Check(forwarded == (context.Response.StatusCode == 204), "Rejected weighted request reached upstream.");
            return (context.Response.StatusCode, long.TryParse(context.Response.Headers.RetryAfter, out var retry) ? retry : 0, System.Text.Encoding.UTF8.GetString(body.ToArray()));
        }
    }
    private sealed class LegacyStore : IRateLimiterStore, IGroupedRateLimiterStore, IDisposable
    {
        private readonly InMemoryRateLimiterStore _inner = new();
        public int Count => _inner.Count;
        public ValueTask<RateLimitLease?> AcquireAsync(string key, Func<RateLimiter> factory, CancellationToken cancellationToken = default) => _inner.AcquireAsync(key, factory, cancellationToken);
        public ValueTask<RateLimitLease?> AcquireGroupedAsync(string group, string policy, string partition, Func<RateLimiter> factory, CancellationToken cancellationToken = default)
            => _inner.AcquireGroupedAsync(group, policy, partition, factory, cancellationToken);
        public bool IsGroupPolicyCompatible(string group, string policy) => _inner.IsGroupPolicyCompatible(group, policy);
        public void Cleanup(TimeSpan threshold, int maximum) => _inner.Cleanup(threshold, maximum);
        public void Dispose() => _inner.Dispose();
    }
}
