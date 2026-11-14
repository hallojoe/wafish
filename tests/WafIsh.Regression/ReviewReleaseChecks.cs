using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WafIsh.Dashboard.Infrastructure.State;
using WafIsh.Models;
using WafIsh.Plugin.Cache;
using WafIsh.Plugin.Metrics;
using WafIsh.Services;
using WafIsh.Storage.Entities;
using WafIsh.Storage;
using WafIsh.Storage.Sqlite;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class ReviewReleaseChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Clock: circuit recovery and cleanup follow monotonic controlled time", () =>
        {
            var clock = new ManualTime(); var store = new InMemoryCircuitStateStore(clock);
            var policy = new CircuitBreakerConfig { MinimumThroughput = 1, RecoveryTimeoutSeconds = 30, SamplingDurationSeconds = 20 };
            using (var attempt = store.TryAcquireAttempt("c", "d", policy)) attempt!.Complete(true);
            clock.ShiftUtc(TimeSpan.FromHours(12));
            Check(store.IsCircuitOpen("c", "d"), "Wall-clock adjustment bypassed circuit recovery.");
            clock.Advance(TimeSpan.FromSeconds(31));
            using (var attempt = store.TryAcquireAttempt("c", "d", policy)) { Check(attempt != null, "Recovery did not advance."); attempt!.Complete(false); }
            clock.Advance(TimeSpan.FromHours(1)); store.CleanupStale(TimeSpan.FromMinutes(5));
            Check(store.Count == 0, "Controlled time did not expire closed circuit state.");
            return Task.CompletedTask;
        });
        await run("Clock: cache freshness ignores wall-clock jumps and expires without waiting", async () =>
        {
            var clock = new ManualTime(); var publisher = new GatewaySnapshotPublisher();
            var route = new RouteConfig { RouteId = "r", Match = new() { Path = "/r" } };
            publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("r", [new("cache", "response-cache", PluginBindingScope.Route, "r", "{\"enabled\":true,\"ttlSeconds\":60}", 1, 1, 0)]) });
            var calls = 0;
            var middleware = new ResponseCacheMiddleware(async c => { calls++; c.Response.Headers.CacheControl = "public,max-age=60"; await c.Response.WriteAsync("cached"); },
                new ResponseCacheStore(Options.Create(new ResponseCacheBudgetOptions()), clock), new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
            async Task Send()
            {
                var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Path = "/r";
                context.Request.Host = new("localhost"); context.Response.Body = new MemoryStream();
                context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
                await middleware.InvokeAsync(context);
            }
            await Send(); clock.ShiftUtc(TimeSpan.FromDays(3)); await Send(); Check(calls == 1, "Wall time expired the cache.");
            clock.Advance(TimeSpan.FromSeconds(61)); await Send(); Check(calls == 2, "Cache did not expire with elapsed time.");
        });
        await run("Clock: registration retry, heartbeat, and shutdown use controlled time", RegistrationClientChecks.ControlledClockAsync);
        await run("Clock: log batch flush and cleanup use the injected timer", async () =>
        {
            await using var host = await ConfigurationTransactionChecks.Fixture.Create();
            var clock = new ManualTime();
            var store = new WafIsh.Plugin.ProxyLog.Services.ProxyLogStore();
            var writer = new WafIsh.Plugin.ProxyLog.Services.SqliteProxyLogWriter(host.App.Services.GetRequiredService<IProxyLogRepository>(),
                NullLogger<WafIsh.Plugin.ProxyLog.Services.SqliteProxyLogWriter>.Instance);
            var service = new WafIsh.Plugin.ProxyLog.Services.AsyncLogPersistenceService(store, writer,
                new WafIsh.Plugin.ProxyLog.Services.ProxyLogRuntimeSettings(Options.Create(new WafIsh.Plugin.ProxyLog.ProxyLogPluginOptions())), null!,
                NullLogger<WafIsh.Plugin.ProxyLog.Services.AsyncLogPersistenceService>.Instance, clock);
            await service.StartResourceAsync(default);
            try
            {
                for (var i = 1; i <= 2; i++)
                {
                    store.Add(new() { Timestamp = DateTime.UtcNow });
                    await Until(() => store.PersistenceReader.Count == 0 && clock.TimerCount > 0);
                    clock.Advance(i == 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromHours(2));
                    await Until(() => service.WrittenCount == i);
                }
            }
            finally { await service.StopResourceAsync(default); }
            Check(store.PendingCount == 0 && store.DroppedCount == 0, "Controlled flush lost records.");
        });
        await run("Metrics: configuration churn retires identities without losing active updates", () =>
        {
            var publisher = new GatewaySnapshotPublisher(); var store = new PluginMetricStore(publisher);
            for (var version = 1; version <= 80; version++)
            {
                publisher.Publish(GatewaySnapshot.Empty with { Version = version,
                    Routes = [new() { RouteId = "keep" }, new() { RouteId = "r" + version }],
                    Clusters = [new() { ClusterId = "c", Destinations = new Dictionary<string, DestinationConfig> { ["d" + version] = new() { Address = "http://localhost/" } } }] });
                Parallel.For(0, 50, _ => store.Record("keep", "c", "d" + version, 200, 1, 1, 1));
                store.Record("r" + version, null, null, 200, 1, 0, 0);
                store.Record("r" + (version - 1), "removed", "gone", 200, 1, 0, 0);
                Check(store.GetRoutes().Count == 2 && store.GetClusters().Count == 1 && store.GetDestinations().Count == 1, "Retired series returned.");
            }
            Check(store.GetRoutes()["keep"].Requests == 4000 && store.GetClusters()["c"].Requests == 4000, "Active counters lost updates.");
            publisher.Publish(GatewaySnapshot.Empty with { Version = 81 });
            Check(store.GetRoutes().Count == 0 && store.GetDestinations().Count == 0, "Snapshot reads retained obsolete series.");
            var bounded = new PluginMetricStore();
            for (var i = 0; i < PluginMetricStore.MaximumSeriesPerScope + 10; i++) bounded.Record(i.ToString(), null, null, 200, 1, 0, 0);
            Check(bounded.GetRoutes().Count == PluginMetricStore.MaximumSeriesPerScope, "Standalone series budget failed.");
            return Task.CompletedTask;
        });
        await run("Storage: UTC migration preserves offset precision, invalid values, and ordering", async () =>
        {
            await using var host = await ConfigurationTransactionChecks.Fixture.Create();
            await host.Sql("DELETE FROM schema_migrations WHERE id='021_utc_timestamps'; INSERT INTO proxy_logs_meta(Timestamp,EventType,Level,StatusCode) VALUES ('2026-10-25T02:30:00.1234567+01:00','ProxyResponse','Info',503),('2026-10-25T03:30:00.1234567+02:00','ProxyResponse','Info',503),('invalid','ProxyResponse','Info',503);");
            await host.Sql("CREATE TABLE utc_migration_fixture (a INTEGER, b INTEGER, created_at TEXT, PRIMARY KEY(a,b)) WITHOUT ROWID; WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<300) INSERT INTO utc_migration_fixture SELECT x,1,CASE WHEN x=300 THEN NULL ELSE '2026-10-25T01:30:00.1234567' END FROM n;");
            await ActivatorUtilities.CreateInstance<SqliteSchemaMigrator>(host.App.Services).StartAsync(default);
            var logs = await host.App.Services.GetRequiredService<IProxyLogRepository>().SearchAsync(1, 10);
            Check(logs.Items.Count(x => x.Timestamp == "2026-10-25T01:30:00.1234567Z") == 2, "UTC migration lost precision or offset meaning.");
            Check(logs.Items.Any(x => x.Timestamp == "invalid"), "Migration deleted an invalid legacy value.");
            await using var connection = host.App.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM utc_migration_fixture WHERE created_at='2026-10-25T01:30:00.1234567Z' OR (a=300 AND created_at IS NULL)";
            Check(Convert.ToInt64(await query.ExecuteScalarAsync()) == 300, "Paged migration lost offsetless values or nulls.");
        });
    }
    internal sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];
        private readonly object _gate = new();
        public int TimerCount { get { lock (_gate) return _timers.Count; } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public void ShiftUtc(TimeSpan duration) { lock (_gate) _utc += duration; }
        public void Advance(TimeSpan duration)
        {
            ManualTimer[] timers;
            lock (_gate) { _ticks += duration.Ticks; _utc += duration; timers = _timers.ToArray(); }
            foreach (var timer in timers) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) { var timer = new ManualTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; }
        }
        private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue, _period;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (clock._gate) { _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks; _period = period.Ticks; return true; } }
            public void Fire() { lock (clock._gate) { if (_due > clock._ticks) return; _due = _period > 0 ? clock._ticks + _period : long.MaxValue; } callback(state); }
            public void Dispose() { lock (clock._gate) { _due = long.MaxValue; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
