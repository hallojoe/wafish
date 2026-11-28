using Wafish.Dashboard.Infrastructure.Plugin;
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

internal static class NamedQuotaChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static RateLimitExecutionConfig Policy(string? group = "shared") => new()
    { Enabled = true, QuotaGroup = group, PartitionKey = "Global", PermitLimit = 2, Window = "1h", TokenLimit = 2, TokensPerPeriod = 2, ReplenishmentPeriod = "1h" };
    private static string Config(RateLimitExecutionConfig config) => JsonSerializer.Serialize(new
    {
        config.Enabled, config.QuotaGroup, config.Algorithm, config.PartitionKey, config.PermitLimit,
        config.Window, config.QueueLimit, config.SegmentsPerWindow, config.TokenLimit,
        config.TokensPerPeriod, config.ReplenishmentPeriod
    }, Json);

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Named quotas: canonical policies validate bounds and ignore derived user values", () =>
        {
            var original = LocalQuotaGroupPolicy.Compile(Policy());
            var equivalent = LocalQuotaGroupPolicy.Compile(Policy() with { Window = "01:00:00", TokenLimit = 999, QuotaGroupPolicy = "forged" });
            Check(original.QuotaGroupPolicy == equivalent.QuotaGroupPolicy, "Equivalent policies differ.");
            Check(LocalQuotaGroupPolicy.Compile(Policy(null) with { QuotaGroupPolicy = "forged" }).QuotaGroupPolicy == null, "Ungrouped config accepted derived identity.");
            foreach (var invalid in new[]
            {
                Policy("") , Policy("Upper"), Policy("trailing\n"), Policy("bad:group"), Policy(new string('a',65)),
                Policy() with { PartitionKey = "unknown" }, Policy() with { Algorithm = (RateLimitAlgorithm)999 },
                Policy() with { QueueLimit = -1 }, Policy() with { QueueLimit = 10001 },
                Policy() with { PermitLimit = 0 }, Policy() with { Window = "" }, Policy() with { Window = "x" },
                Policy() with { Window = "NaNs" }, Policy() with { Window = "1q" }, Policy() with { Window = "2d" },
                Policy() with { Window = "00:00:00" }, Policy() with { Window = "2.00:00:00" },
                Policy() with { Algorithm = RateLimitAlgorithm.SlidingWindow, SegmentsPerWindow = 1 },
                Policy() with { Algorithm = RateLimitAlgorithm.TokenBucket, TokenLimit = 0 },
                Policy() with { Algorithm = RateLimitAlgorithm.TokenBucket, TokensPerPeriod = 1000001 }
            }) Reject(() => LocalQuotaGroupPolicy.Compile(invalid));
            foreach (var algorithm in Enum.GetValues<RateLimitAlgorithm>())
                Check(LocalQuotaGroupPolicy.Compile(Policy() with { Algorithm = algorithm }).QuotaGroupPolicy?.Length == 64, "Policy identity missing.");
            Check(original.QuotaGroupPolicy != LocalQuotaGroupPolicy.Compile(Policy() with { PartitionKey = "IpAddress" }).QuotaGroupPolicy, "Partition changes did not change identity.");
            return Task.CompletedTask;
        });

        await run("Named quotas: routes share permits while groups and client partitions remain isolated", async () =>
        {
            foreach (var algorithm in new[] { RateLimitAlgorithm.FixedWindow, RateLimitAlgorithm.SlidingWindow, RateLimitAlgorithm.TokenBucket })
            foreach (var partition in new[] { "Global", "Route", "IpAddress", "UserId" })
            {
                using var store = new InMemoryRateLimiterStore();
                var harness = new Harness(store);
                var config = Policy() with { Algorithm = algorithm, PartitionKey = partition };
                harness.Publish(("a", config), ("b", config), ("other", config with { QuotaGroup = "other" }), ("local", config with { QuotaGroup = null }));
                Check(await harness.Request("a") == 204 && await harness.Request("b") == 204, "Initial shared permits failed.");
                Check(await harness.Request("a") == 429 && await harness.Request("b") == 429, "Grouped routes obtained separate quotas.");
                Check(await harness.Request("other") == 204 && await harness.Request("local") == 204, "Unrelated quota was consumed.");
                if (partition is "IpAddress" or "UserId") Check(await harness.Request("b", client: "second") == 204, "Client partitions were combined.");
                harness.Publish(("renamed", config), ("new-route", config));
                Check(await harness.Request("renamed") == 429 && await harness.Request("new-route") == 429, "Route changes reset a named quota.");
                harness.Publish();
                harness.Publish(("recreated", config));
                Check(await harness.Request("recreated") == 429, "Removing and recreating bindings reset a quota.");
                harness.Publish(("changed", config with { PermitLimit = 99, TokenLimit = 99 }));
                Check(await harness.Request("changed") == 503, "A stale configuration race bypassed policy ownership.");
            }
        });

        await run("Named quotas: concurrency ownership spans routes and queued cancellation", async () =>
        {
            using var store = new InMemoryRateLimiterStore();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var harness = new Harness(store, async context => { entered.TrySetResult(); await release.Task; context.Response.StatusCode = 204; });
            var policy = Policy() with { Algorithm = RateLimitAlgorithm.Concurrency, PermitLimit = 1, QueueLimit = 1 };
            harness.Publish(("a", policy), ("b", policy));
            var first = harness.Request("a");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var queued = harness.Request("b", cancellation: cancellation.Token);
            cancellation.Cancel();
            try { await queued; throw new Exception("Queued request ignored cancellation."); } catch (OperationCanceledException) { }
            release.SetResult();
            Check(await first == 204 && await harness.Request("b") == 204, "Concurrency permits were not released.");
            using var legacy = new LegacyStore();
            var unsupported = new Harness(legacy);
            unsupported.Publish(("a", Policy()));
            Check(await unsupported.Request("a") == 503, "Unsupported custom store silently ignored group enforcement.");
        });

        await run("Named quotas: idle cleanup retires policy ownership without dropping active leases", async () =>
        {
            var clock = new Clock();
            using var store = new InMemoryRateLimiterStore(clock);
            var firstLimiter = new ControlledLimiter();
            var secondLimiter = new ControlledLimiter();
            using var first = await store.AcquireGroupedAsync("group", "old", "one", () => firstLimiter);
            using (var second = await store.AcquireGroupedAsync("group", "old", "two", () => secondLimiter)) { }
            Check(store.Count == 2 && !store.IsGroupPolicyCompatible("group", "new"), "Policy ownership was not retained.");
            clock.Advance(TimeSpan.FromHours(1));
            store.Cleanup(TimeSpan.FromMinutes(5), 0);
            Check(store.Count == 1 && secondLimiter.Disposals == 1 && firstLimiter.Disposals == 0, "Cleanup broke active ownership.");
            Check(!store.IsGroupPolicyCompatible("group", "new"), "One partition released the entire group.");
            first!.Dispose();
            clock.Advance(TimeSpan.FromHours(1));
            firstLimiter.Idle = null;
            store.Cleanup(TimeSpan.FromMinutes(5), 0);
            Check(store.Count == 1, "Partially consumed quota was evicted.");
            firstLimiter.Idle = TimeSpan.FromHours(1);
            store.Cleanup(TimeSpan.FromMinutes(5), 0);
            Check(store.Count == 0 && store.IsGroupPolicyCompatible("group", "new"), "Retired group retained its policy forever.");
            var lateLimiter = new ControlledLimiter();
            using var late = await store.AcquireGroupedAsync("group", "new", "one", () => lateLimiter);
            store.Dispose();
            Check(lateLimiter.Disposals == 0, "Store disposed an active group lease.");
            late!.Dispose(); late.Dispose();
            Check(lateLimiter.Disposals == 1, "Deferred limiter disposal was not exactly once.");
            try { store.IsGroupPolicyCompatible("group", "new"); throw new Exception("Disposed store accepted validation."); } catch (ObjectDisposedException) { }
            try { await store.AcquireGroupedAsync("group", "new", "one", () => new ControlledLimiter()); throw new Exception("Disposed store accepted acquisition."); } catch (ObjectDisposedException) { }
        });

        await run("Named quotas: concurrent policies cannot fork counters and admission remains bounded", async () =>
        {
            using var store = new InMemoryRateLimiterStore();
            try { await store.AcquireGroupedAsync("failed", "old", "one", () => throw new InvalidOperationException("factory")); } catch (InvalidOperationException) { }
            Check(store.Count == 0 && store.IsGroupPolicyCompatible("failed", "new"), "Failed construction pinned policy state.");
            var outcomes = await Task.WhenAll(new[] { "one", "two" }.Select(policy => Task.Run(async () =>
            {
                try { using var lease = await store.AcquireGroupedAsync("race", policy, "partition", () => new ControlledLimiter()); return true; }
                catch (RateLimitGroupPolicyConflictException) { return false; }
            })));
            Check(outcomes.Count(x => x) == 1 && store.Count == 1, "Conflicting policies both acquired counters.");
            for (var i = 1; i < InMemoryRateLimiterStore.MaximumPartitions; i++)
                using (await store.AcquireGroupedAsync("bounded", "policy", i.ToString(), () => new ControlledLimiter())) { }
            Check(await store.AcquireGroupedAsync("overflow", "new", "one", () => throw new Exception("Capacity invoked factory")) == null, "Grouped capacity was unbounded.");
            Check(await store.AcquireAsync("local-overflow", () => throw new Exception("Capacity invoked factory")) == null, "Grouped and local capacity were separate.");
            Check(store.IsGroupPolicyCompatible("overflow", "other"), "Capacity failure retained a group.");
        });

        await run("Named quotas: compiler rejects conflicts and previews allocate no counters", () =>
        {
            using var store = new InMemoryRateLimiterStore();
            var compiler = new RateLimitGroupCompiler(store);
            CompiledRoutePlugin Compile(string route, RateLimitExecutionConfig config) => compiler.Compile(new("id-" + route, "rate-limit", PluginBindingScope.Route, route, Config(config), 1, 1, 0), new() { RouteId = route });
            GatewaySnapshot Snapshot(params CompiledRoutePlugin[] plans) => GatewaySnapshot.Empty with
            { RouteExecutionPlans = plans.GroupBy(p => p.Route.RouteId).ToImmutableDictionary(g => g.Key, g => new RouteExecutionPlan(g.Key, g.ToImmutableArray())) };
            var valid = Compile("a", Policy());
            compiler.Validate(Snapshot(valid, Compile("b", Policy() with { Window = "01:00:00" })));
            Check(store.Count == 0, "Preview allocated quota state.");
            Reject(() => compiler.Validate(Snapshot(valid, Compile("b", Policy() with { PermitLimit = 3 }))));
            Reject(() => compiler.Validate(Snapshot(valid, Compile("a", Policy()))));
            Reject(() => new RateLimitGroupCompiler().Compile(valid.Binding, valid.Route));
            Reject(() => compiler.Compile(valid.Binding with { SchemaVersion = 2 }, valid.Route));
            compiler.Validate(Snapshot(Compile("disabled", Policy() with { Enabled = false })));
            return Task.CompletedTask;
        });

        await run("Named quotas: real binding mutations reject conflicts before persistence and preserve remaining permits", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start();
            var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            var repository = fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var mutations = fixture.App.Services.GetRequiredService<IPluginBindingMutationService>();
            foreach (var id in new[] { "one", "two" })
            {
                Check((await routes.TryAddRouteConfig(new() { RouteId = id, Match = new() { Path = "/" + id } })).Success, "Route creation failed.");
                await fixture.Bind(id, "redirect", "{\"target\":\"/allowed\"}");
                await fixture.WaitRoute(id);
            }
            var one = await fixture.Bind("one", "rate-limit", Config(Policy()));
            await RejectAsync(() => fixture.Bind("two", "rate-limit", Config(Policy() with { PermitLimit = 3 })));
            Check((await repository.GetBindingsAsync()).Count(b => b.PluginId == "rate-limit") == 1, "Rejected group conflict persisted.");
            var two = await fixture.Bind("two", "rate-limit", Config(Policy()));
            using (var response = await fixture.Client.GetAsync("/one")) Check((int)response.StatusCode == 307, "First grouped route failed.");
            using (var response = await fixture.Client.GetAsync("/two")) Check((int)response.StatusCode == 307, "Second grouped route failed.");
            using (var response = await fixture.Client.GetAsync("/two")) Check((int)response.StatusCode == 429, "HTTP routes did not share permits.");
            await mutations.DeleteAsync(two.Id);
            await mutations.DeleteAsync(one.Id);
            await RejectAsync(() => fixture.Bind("one", "rate-limit", Config(Policy() with { PermitLimit = 3 })));
            Check(!(await repository.GetBindingsAsync()).Any(b => b.PluginId == "rate-limit"), "Retained-state conflict persisted.");
            await fixture.Bind("one", "rate-limit", Config(Policy()));
            using (var response = await fixture.Client.GetAsync("/one")) Check((int)response.StatusCode == 429, "Binding recreation reset quota state.");
        });
    }

    private sealed class Harness(IRateLimiterStore store, RequestDelegate? next = null)
    {
        private readonly GatewaySnapshotPublisher _publisher = new();
        private long _version;
        private Dictionary<string, RouteConfig> _routes = [];
        public void Publish(params (string Id, RateLimitExecutionConfig Config)[] routes)
        {
            _routes = routes.ToDictionary(p => p.Id, p => new RouteConfig { RouteId = p.Id, Match = new() { Path = "/" + p.Id } });
            _publisher.Publish(GatewaySnapshot.Empty with { Version = ++_version, Routes = _routes.Values.ToImmutableArray(),
                RoutePlugins = routes.ToImmutableDictionary(p => p.Id, p => ImmutableArray.Create(new PluginBindingSnapshot("binding-" + p.Id, "rate-limit", PluginBindingScope.Route, p.Id, Config(p.Config), 1, 1, 0))) });
        }
        public async Task<int> Request(string route, string client = "first", CancellationToken cancellation = default)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/" + route;
            context.RequestAborted = cancellation;
            context.Connection.RemoteIpAddress = IPAddress.Parse(client == "first" ? "127.0.0.1" : "127.0.0.2");
            context.User = new(new ClaimsIdentity([new(ClaimTypes.Name, client)], "test"));
            await using var body = new MemoryStream(); context.Response.Body = body;
            context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(_routes[route], null!, HttpTransformer.Default) });
            var middleware = new RateLimitMiddleware(next ?? (c => { c.Response.StatusCode = 204; return Task.CompletedTask; }),
                NullLogger<RateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store,
                new GatewayPluginExecutionPlanProvider(_publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
            await middleware.InvokeAsync(context);
            return context.Response.StatusCode;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
    private sealed class ControlledLimiter : RateLimiter
    {
        private readonly ConcurrencyLimiter _inner = new(new() { PermitLimit = 1, QueueLimit = 1 });
        public int Disposals;
        public TimeSpan? Idle { get; set; } = TimeSpan.FromHours(1);
        public override TimeSpan? IdleDuration => Idle;
        public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int count) => _inner.AttemptAcquire(count);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int count, CancellationToken cancellation) => _inner.AcquireAsync(count, cancellation);
        protected override void Dispose(bool disposing) { if (disposing) { Disposals++; _inner.Dispose(); } }
    }
    private sealed class LegacyStore : IRateLimiterStore, IDisposable
    {
        private readonly InMemoryRateLimiterStore _inner = new();
        public int Count => _inner.Count;
        public ValueTask<RateLimitLease?> AcquireAsync(string key, Func<RateLimiter> factory, CancellationToken cancellationToken = default) => _inner.AcquireAsync(key, factory, cancellationToken);
        public void Cleanup(TimeSpan threshold, int maximum) => _inner.Cleanup(threshold, maximum);
        public void Dispose() => _inner.Dispose();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); throw new Exception("Invalid quota configuration was accepted."); } catch (ArgumentException) { } }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); throw new Exception("Invalid quota configuration was persisted."); } catch (ArgumentException) { } }
}
