using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugin.RateLimit;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.Plugins;
using Wafish.Models;

internal static class LifetimeStressChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Lifetime: copied leases release once and direct disposal drains requests", CheckLeaseAsync);
        await run("Lifetime: 64 replacements preserve overlapping requests through shutdown", CheckReplacementAsync);
        await run("Lifetime: collectible contexts unload after success and shutdown faults", CheckCollectibleAsync);
        await run("Lifetime: external resources stop before domain dependencies are disposed", CheckExternalDomainAsync);
        await run("Lifetime: limiter rejection status and headers match each algorithm", CheckHeadersAsync);
        await run("Lifetime: concurrent legacy deactivation and host shutdown share ownership", CheckLegacyAsync);
        await run("Lifetime: half-open probe contention and cancellation preserve the limit", CheckProbesAsync);
        await run("Lifetime: native limiter queues cancel and replenish at algorithm boundaries", CheckAlgorithmsAsync);
        await run("Lifetime: partition churn preserves active quotas and bounds retained state", CheckChurnAsync);
    }

    private static async Task CheckLeaseAsync()
    {
        var resource = new OwnedResource();
        var domain = new PluginRuntimeDomain(new ServiceCollection().BuildServiceProvider(), new Dictionary<string, IGatewayPlugin>(),
            _ => Task.CompletedTask, _ => Task.CompletedTask, [resource]);
        Check(domain.TryAcquire(out var first), "First lease acquisition failed.");
        Check(domain.TryAcquire(out var second), "Second lease acquisition failed.");
        var copy = first;
        first.Dispose(); copy.Dispose();
        var shutdown = domain.DisposeAsync().AsTask();
        Check(!shutdown.IsCompleted && !domain.TryAcquire(out _), "Disposal ignored the remaining request.");
        second.Dispose(); second.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await domain.DisposeAsync();
        Check(resource.Disposals == 1, "Resource disposal count changed.");
    }

    private static async Task CheckReplacementAsync()
    {
        var plugin = new OwnedPlugin();
        await using var root = new ServiceCollection().AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Directory.GetCurrentDirectory()))
            .AddSingleton<IPluginResourceMonitor, Monitor>()
            .AddSingleton<IGatewayPlugin>(plugin).BuildServiceProvider();
        await using var host = new ExternalGatewayPluginHost(root.GetRequiredService<IHostEnvironment>(),
            root.GetRequiredService<IConfiguration>(), NullLogger<ExternalGatewayPluginHost>.Instance);
        await using var manager = new PluginRuntimeDomainManager(root, host, NullLogger<PluginRuntimeDomainManager>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<Task>();
        try
        {
            for (var cycle = 0; cycle < 64; cycle++)
            {
                Check((await manager.TransitionAsync([plugin.PluginId])).Succeeded, "Replacement failed.");
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                requests.Add(manager.InvokeMiddlewareAsync(new DefaultHttpContext(), async _ =>
                {
                    entered.SetResult();
                    await release.Task;
                }));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var shutdown = manager.DisposeAsync().AsTask();
            Check(!shutdown.IsCompleted && plugin.Resources.All(r => r.Disposals == 0), "A request lost its provider.");
            release.SetResult();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Check(plugin.Resources.Count == 64 && plugin.Resources.All(r => r.Disposals == 1), "Replacement leaked a provider.");
        }
        finally { release.TrySetResult(); await Task.WhenAll(requests); }
    }

    private static async Task CheckCollectibleAsync()
    {
        using var fixture = new PluginFixture();
        await using var host = fixture.Host();
        var retained = new List<ExternalPluginRuntimeLoad>();
        var weak = new List<WeakReference>();
        for (var cycle = 0; cycle < 16; cycle++)
        {
            var result = await LoadAndReleaseAsync(host, cycle % 2 == 1);
            retained.Add(result.Owner); weak.Add(result.Context);
        }
        await Task.Yield(); // Let the final disposal continuation leave the stack before forcing collection.
        await CollectAsync(weak);
        Check(weak.All(w => !w.IsAlive), "Disposed handles retained collectible cycles: " + string.Join(",", weak.Select((w, i) => (w, i)).Where(x => x.w.IsAlive).Select(x => x.i)));
        Check(host.Registrations.Single(r => r.Manifest.Id == "collectible-probe").Status == ExternalPluginRegistrationStatus.Unloaded,
            "Unload status did not update.");
        GC.KeepAlive(retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ExternalPluginRuntimeLoad Owner, WeakReference Context)> LoadAndReleaseAsync(ExternalGatewayPluginHost host, bool fail)
    {
        var owner = host.LoadRuntime("collectible-probe");
        var weak = new WeakReference(AssemblyLoadContext.GetLoadContext(owner.Plugin.GetType().Assembly));
        var events = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        owner.Plugin.ConfigureServices(services, Tuple.Create<Action<string>, Task, bool>(events.Enqueue, release.Task, fail));
        await using var provider = services.BuildServiceProvider();
        owner.Plugin.ConfigureMiddleware(new ApplicationBuilder(provider));
        await ((IPluginRuntimeResource)owner.Plugin).StartResourceAsync(CancellationToken.None);
        var shutdown = owner.DisposeAsync().AsTask();
        Check(!shutdown.IsCompleted && ReferenceEquals(shutdown, owner.DisposeAsync().AsTask()), "Disposal did not retain its task.");
        release.SetResult();
        try { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); Check(!fail, "Plugin failures were hidden."); }
        catch (AggregateException failure) when (fail) { Check(failure.InnerExceptions.Count == 2, "Shutdown skipped disposal after stop failed."); }
        Check(events.SequenceEqual(["start", "stop-start", "stop-end", "dispose"]), "Plugin shutdown order changed.");
        try { _ = owner.Plugin; throw new Exception("Disposed handle exposed a plugin."); }
        catch (ObjectDisposedException) { }
        return (owner, weak);
    }

    private static async Task CheckExternalDomainAsync()
    {
        using var fixture = new PluginFixture();
        await using var host = fixture.Host();
        var owner = host.LoadRuntime("collectible-probe");
        var events = new ConcurrentQueue<string>();
        var services = new ServiceCollection();
        owner.Plugin.ConfigureServices(services, Tuple.Create<Action<string>, Task, bool>(events.Enqueue, Task.CompletedTask, false));
        var provider = services.BuildServiceProvider();
        owner.Plugin.ConfigureMiddleware(new ApplicationBuilder(provider));
        var domain = new PluginRuntimeDomain(provider, new Dictionary<string, IGatewayPlugin> { ["collectible-probe"] = owner.Plugin },
            _ => Task.CompletedTask, _ => Task.CompletedTask, [owner]);
        await domain.DisposeAsync();
        Check(events.SequenceEqual(["stop-start", "stop-end", "dispose"]), "Domain stopped resources after their dependencies.");
    }

    private static async Task CheckHeadersAsync()
    {
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket", "Concurrency" })
        {
            var route = new RouteConfig { RouteId = "limit", Match = new() { Path = "/limit" } };
            var publisher = new GatewaySnapshotPublisher();
            publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                    [new("limit", "rate-limit", PluginBindingScope.Route, route.RouteId,
                        JsonSerializer.Serialize(new { enabled = true, algorithm, permitLimit = 1, window = "00:01:00", queueLimit = 0,
                            tokenLimit = 1, tokensPerPeriod = 1, replenishmentPeriod = "00:02:00" }), 1, 1, 0)]) });
            using var store = new InMemoryRateLimiterStore();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwarded = 0;
            var middleware = new RateLimitMiddleware(async _ => { Interlocked.Increment(ref forwarded); entered.TrySetResult(); await release.Task; },
                NullLogger<RateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store,
                new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
            DefaultHttpContext Context()
            {
                var context = new DefaultHttpContext(); context.Request.Path = "/limit";
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                context.Response.Body = new MemoryStream();
                context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
                return context;
            }
            var first = Context(); var accepted = middleware.InvokeAsync(first);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var rejected = Context();
                await middleware.InvokeAsync(rejected);
                var retryAfter = algorithm == "Concurrency" ? "1" : algorithm == "TokenBucket" ? "120" : "60";
                Check(rejected.Response.StatusCode == 429 && rejected.Response.Headers.RetryAfter == retryAfter,
                    algorithm + " rejection lost retry guidance.");
                using var body = JsonDocument.Parse(((MemoryStream)rejected.Response.Body).ToArray());
                Check(body.RootElement.GetProperty("retryAfter").GetInt32().ToString() == retryAfter && forwarded == 1,
                    algorithm + " rejected request forwarded or returned inconsistent guidance.");
                await rejected.Response.Body.DisposeAsync();
            }
            finally { release.TrySetResult(); await accepted; await first.Response.Body.DisposeAsync(); }
        }
    }

    private static async Task CheckLegacyAsync()
    {
        using var fixture = new PluginFixture();
        var result = await ReleaseLegacyAsync(fixture);
        await Task.Yield();
        await CollectAsync(result.Contexts);
        Check(result.Contexts.All(w => !w.IsAlive), "Legacy shutdown retained collectible plugins.");
        GC.KeepAlive(result.Host);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ExternalGatewayPluginHost Host, WeakReference[] Contexts)> ReleaseLegacyAsync(PluginFixture fixture)
    {
        var host = fixture.Host();
        var manifests = host.Manifests.ToDictionary(m => m.Id);
        Check(host.TryActivate("collectible-probe", manifests, out _), "Legacy activation failed.");
        Check(host.TryActivate("collectible-sync", manifests, out _), "Sync activation failed.");
        var weak = host.LoadedPlugins.Select(p => new WeakReference(AssemblyLoadContext.GetLoadContext(p.GetType().Assembly))).ToArray();
        var events = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var plugin in host.LoadedPlugins)
        {
            plugin.ConfigureServices(new ServiceCollection(), plugin.PluginId == "collectible-probe"
                ? Tuple.Create<Action<string>, Task, bool>(events.Enqueue, release.Task, false) : (Action<string>)events.Enqueue);
        }
        var deactivate = host.DeactivateAsync("collectible-probe").AsTask();
        var shutdown = host.DisposeAsync().AsTask();
        Check(!shutdown.IsCompleted, "Host abandoned active deactivation.");
        release.SetResult();
        await Task.WhenAll(deactivate, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
        await host.DisposeAsync();
        host.Deactivate("missing");
        Check(events.Count(e => e == "dispose") == 2 && events.Count(e => e == "stop-end") == 1, "Legacy cleanup repeated or skipped work.");
        Check(!host.TryActivate("collectible-sync", manifests, out _), "Disposed host accepted activation.");
        return (host, weak);
    }

    private static async Task CheckProbesAsync()
    {
        var time = new ReviewReleaseChecks.ManualTime();
        var store = new InMemoryCircuitStateStore(time);
        var policy = new CircuitBreakerConfig { Enabled = true, MinimumThroughput = 1, FailureRatio = .5,
            RecoveryTimeoutSeconds = 30, HalfOpenMaxAttempts = 4 };
        store.RecordOutcome("cluster", "destination", true, config: policy);
        Check(store.TryAcquireAttempt("cluster", "destination", policy) == null, "Open circuit admitted traffic.");
        time.Advance(TimeSpan.FromSeconds(30));
        for (var wave = 0; wave < 32; wave++)
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Task.Run(() => store.TryAcquireAttempt("cluster", "destination", policy))));
            Check(attempts.Count(a => a != null) == 4, "Half-open probe limit changed under contention.");
            foreach (var attempt in attempts) { attempt?.Dispose(); attempt?.Dispose(); }
        }
        using var successful = store.TryAcquireAttempt("cluster", "destination", policy);
        Check(successful != null, "Cancellation consumed the remaining probe slots.");
        successful!.Complete(false);
        Check(!store.IsCircuitOpen("cluster", "destination"), "Successful probe did not close the circuit.");
    }

    private static async Task CheckAlgorithmsAsync()
    {
        foreach (var algorithm in new[] { "fixed", "sliding", "token", "concurrency" })
        {
            using RateLimiter limiter = algorithm switch
            {
                "fixed" => new FixedWindowRateLimiter(new() { PermitLimit = 4, Window = TimeSpan.FromMilliseconds(40), QueueLimit = 8, AutoReplenishment = false }),
                "sliding" => new SlidingWindowRateLimiter(new() { PermitLimit = 4, Window = TimeSpan.FromMilliseconds(40), SegmentsPerWindow = 2, QueueLimit = 8, AutoReplenishment = false }),
                "token" => new TokenBucketRateLimiter(new() { TokenLimit = 4, TokensPerPeriod = 4, ReplenishmentPeriod = TimeSpan.FromMilliseconds(40), QueueLimit = 8, AutoReplenishment = false }),
                _ => new ConcurrencyLimiter(new() { PermitLimit = 4, QueueLimit = 8 })
            };
            var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => await limiter.AcquireAsync(1))));
            Check(leases.All(l => l.IsAcquired), algorithm + " initial permits missing.");
            using (var rejected = limiter.AttemptAcquire(1)) Check(!rejected.IsAcquired, algorithm + " exceeded its permit count.");
            using var cancel = new CancellationTokenSource();
            var queued = Enumerable.Range(0, 8).Select(_ => limiter.AcquireAsync(1, cancel.Token).AsTask()).ToArray();
            Check(queued.All(q => !q.IsCompleted), algorithm + " queue did not hold requests.");
            using (var rejected = await limiter.AcquireAsync(1)) Check(!rejected.IsAcquired, algorithm + " exceeded its queue limit.");
            cancel.Cancel();
            foreach (var request in queued)
            {
                try { using var unexpected = await request; throw new Exception("Queued cancellation was ignored."); }
                catch (OperationCanceledException) { }
            }
            var replacement = limiter.AcquireAsync(1).AsTask();
            foreach (var lease in leases) lease.Dispose();
            if (limiter is ReplenishingRateLimiter replenishing)
            {
                // Native limiters own their monotonic clock. Poll actual replenishment with a watchdog.
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (!replacement.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
                {
                    replenishing.TryReplenish(); await Task.Yield();
                }
            }
            using var acquired = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            Check(acquired.IsAcquired, algorithm + " did not replenish after cancellation.");
        }
    }

    private static async Task CheckChurnAsync()
    {
        var time = new ReviewReleaseChecks.ManualTime();
        using var store = new InMemoryRateLimiterStore(time);
        var created = new ConcurrentBag<ControlledLimiter>();
        RateLimiter Factory() { var limiter = new ControlledLimiter(); created.Add(limiter); return limiter; }
        var active = await store.AcquireAsync("active", Factory);
        for (var wave = 0; wave < 3; wave++)
        {
            for (var partition = 0; partition < InMemoryRateLimiterStore.MaximumPartitions - 1; partition++)
            {
                using var lease = await store.AcquireAsync($"{wave}:{partition}", Factory);
                Check(lease?.IsAcquired == true, "Bounded partition admission failed.");
            }
            Check(await store.AcquireAsync("overflow", Factory) == null, "Partition admission exceeded its bound.");
            time.Advance(TimeSpan.FromMinutes(10));
            store.Cleanup(TimeSpan.FromMinutes(5), 0);
            Check(store.Count == 1 && active!.IsAcquired, "Cleanup removed an active partition.");
        }
        using var cancel = new CancellationTokenSource();
        var queued = store.AcquireAsync("active", Factory, cancel.Token).AsTask();
        Check(!queued.IsCompleted, "Active quota was reset during cleanup.");
        store.Dispose();
        Check(created.Count(c => c.Disposals == 0) == 1, "Shutdown disposed an owned limiter.");
        cancel.Cancel();
        try { using var unexpected = await queued; throw new Exception("Queue cancellation failed."); }
        catch (OperationCanceledException) { }
        active!.Dispose(); active.Dispose();
        Check(created.Count == 5998 && created.All(c => c.Disposals == 1), "Partition churn leaked or repeated limiter disposal.");
    }

    private static async Task CollectAsync(IEnumerable<WeakReference> references)
    {
        for (var pass = 0; pass < 12 && references.Any(r => r.IsAlive); pass++)
        {
            await Task.Yield();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
    }
    private sealed class PluginFixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "wafish-collectible-" + Guid.NewGuid().ToString("N"));
        public PluginFixture()
        {
            var root = Directory.GetCurrentDirectory();
            while (!File.Exists(Path.Combine(root, "Wafish.slnx"))) root = Directory.GetParent(root)!.FullName;
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            foreach (var (id, type) in new[] { ("collectible-probe", "ProbePlugin"), ("collectible-sync", "SyncProbePlugin") })
            {
                var directory = Path.Combine(_path, id); Directory.CreateDirectory(directory);
                File.Copy(Path.Combine(root, "tests/Fixtures/Wafish.CollectiblePlugin/bin", configuration, "net10.0/Wafish.CollectiblePlugin.dll"), Path.Combine(directory, "Wafish.CollectiblePlugin.dll"));
                File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new { id, name = id, version = "1.0.0",
                    entryAssembly = "Wafish.CollectiblePlugin.dll", entryType = "Wafish.RegressionFixtures." + type }));
            }
        }
        public ExternalGatewayPluginHost Host() => new(new TestHostEnvironment(_path), new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Gateway:Dashboard:PluginDirectory"] = _path }).Build(), NullLogger<ExternalGatewayPluginHost>.Instance);
        public void Dispose() => Directory.Delete(_path, true);
    }
    private sealed class OwnedPlugin : IGatewayPlugin
    {
        public List<OwnedResource> Resources { get; } = [];
        public string PluginId => "waf";
        public string DisplayName => "Lifetime fixture";
        public string Version => "1.0.0";
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton(_ => { var resource = new OwnedResource(); Resources.Add(resource); return resource; });
        public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) => ConfigureServices(services);
        public void ConfigureMiddleware(IApplicationBuilder app)
        {
            var resource = app.ApplicationServices.GetRequiredService<OwnedResource>();
            app.Use(async (_, next) => { Check(resource.Disposals == 0, "Disposed provider entered."); await next(); Check(resource.Disposals == 0, "Provider disposed during request."); });
        }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    }
    private sealed class OwnedResource : IAsyncDisposable
    {
        public int Disposals;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
    private sealed class Monitor : IPluginResourceMonitor
    {
        public IReadOnlyList<PluginResourceUsage> GetAllUsage() => [];
        public PluginResourceUsage? GetUsage(string pluginId) => null;
        public void RecordRequest(string pluginId, long elapsedMs, bool succeeded) { }
        public PluginResourceUsageTotals GetTotals() => new(0, 0, 0, 0, 0, 0, 0);
    }
    private sealed class ControlledLimiter : RateLimiter
    {
        private readonly ConcurrencyLimiter _inner = new(new() { PermitLimit = 1, QueueLimit = 1 });
        public int Disposals;
        public override TimeSpan? IdleDuration => TimeSpan.FromHours(1);
        public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => _inner.AttemptAcquire(permitCount);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) => _inner.AcquireAsync(permitCount, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) { Interlocked.Increment(ref Disposals); _inner.Dispose(); } }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
