using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Notifications;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Infrastructure.Performance;
using Wafish.Plugin.Metrics;
using Wafish.Plugin.ProxyLog;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Plugin.ProxyLog.Services;
using Wafish.Plugins;
using Wafish.Runtime;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Wafish.Storage.Sqlite;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using Fixture = ConfigurationTransactionChecks.Fixture;

internal static class ReviewAccountingChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        foreach (var mode in new[] { "cancel-write", "cancel-fill", "exhaust-retries", "transient", "cancel-drain" })
        {
            await run("Accounting: log batches preserve ownership during " + mode, async () =>
            {
                await using var host = await Fixture.Create();
                var inner = host.App.Services.GetRequiredService<IProxyLogRepository>();
                var repo = DispatchProxy.Create<IProxyLogRepository, LogRepositoryProxy>();
                var proxy = (LogRepositoryProxy)repo;
                proxy.Inner = inner;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                proxy.BeforeWrite = async ct =>
                {
                    var call = Interlocked.Increment(ref calls);
                    if (mode == "exhaust-retries" || mode == "transient" && call == 1)
                    {
                        throw new IOException("Injected write failure");
                    }

                    if ((mode is "cancel-write" or "cancel-drain") && call == 1)
                    {
                        entered.SetResult();
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                };
                var store = new ProxyLogStore(channelCapacity: 100);
                var service = new AsyncLogPersistenceService(store,
                    new SqliteProxyLogWriter(repo, NullLogger<SqliteProxyLogWriter>.Instance),
                    new ProxyLogRuntimeSettings(Options.Create(new ProxyLogPluginOptions())), null!, NullLogger<AsyncLogPersistenceService>.Instance);
                var count = mode == "cancel-fill" ? 1 : 100;
                for (var i = 0; i < count; i++)
                {
                    store.Add(new() { Timestamp = DateTime.UtcNow, EventType = LogEventType.ProxyResponse, StatusCode = 200 });
                }

                await service.StartResourceAsync(default);
                if (mode is "cancel-write" or "cancel-drain")
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (mode == "cancel-fill")
                {
                    await Until(() => store.PersistenceReader.Count == 0);
                }
                else
                {
                    await Until(() => service.WrittenCount + service.DroppedCount == count);
                }

                if (mode == "cancel-drain")
                {
                    try { await service.StopResourceAsync(new CancellationToken(true)); }
                    catch (OperationCanceledException) { }
                }
                await service.StopResourceAsync(default);
                var stored = (await inner.SearchAsync(1, 200)).TotalCount;
                var discarded = mode is "exhaust-retries" or "cancel-drain" ? count : 0;
                Check(stored == count - discarded && service.WrittenCount == stored && service.DroppedCount == discarded,
                    $"Unaccounted or duplicate batch: stored={stored}, written={service.WrittenCount}, dropped={service.DroppedCount}.");
                Check(store.PendingCount == 0 && store.SubmittedCount == stored + discarded, "Persistence accounting did not balance.");
                var health = await service.CheckHealthAsync();
                if (mode == "exhaust-retries")
                {
                    Check(health.Statistics["failures"] == 3, "Write failures were not counted.");
                }
            });
        }

        await run("Accounting: concurrent notification overflow and shutdown count every event", async () =>
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            using var dispatcher = new NotificationDispatcher(services, NullLogger<NotificationDispatcher>.Instance);
            Parallel.For(0, 2500, i => dispatcher.EnqueueAlert("test", i.ToString(), "test"));
            var before = dispatcher.GetSnapshot();
            Check(before.Submitted == 2500 && before.Queued == 1000 && before.Discarded == 1500, "DropOldest accounting was inaccurate.");
            await dispatcher.StartAsync(default);
            await dispatcher.StopAsync(default);
            dispatcher.EnqueueConfigChange("test", null, null);
            var after = dispatcher.GetSnapshot();
            Check(after.Submitted == 2501 && after.Discarded == 2501 && after.Queued == 0 && after.InFlight == 0, "Shutdown lost queued events.");
            using var neverStarted = new NotificationDispatcher(services, NullLogger<NotificationDispatcher>.Instance);
            neverStarted.EnqueueAlert("test", null, "test");
            await neverStarted.StopAsync(default);
            Check(neverStarted.GetSnapshot().Discarded == 1, "Unstarted dispatcher retained events after shutdown.");
        });

        await run("Accounting: notifications classify delivery, suppression, failure, and cancellation", async () =>
        {
            var settings = new WebhookSettingsData { Platforms = new() { ["generic"] = [new() { Url = "https://example.test/hooks" }] },
                EnabledEvents = ["test"], RetryCount = 0, CooldownSeconds = 0 };
            var repository = new NotificationSettings(settings);
            using var handler = new NotificationHandler();
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IWebhookSettingsRepository>(repository);
            services.AddSingleton<IHttpClientFactory>(new TestHttpFactory(handler));
            services.AddSingleton<WebhookNotificationService>();
            using var provider = services.BuildServiceProvider();
            using var dispatcher = new NotificationDispatcher(provider, NullLogger<NotificationDispatcher>.Instance);
            await dispatcher.StartAsync(default);
            dispatcher.EnqueueAlert("test", "one", "test");
            await Until(() => dispatcher.GetSnapshot().Delivered == 1);
            dispatcher.EnqueueAlert("unsubscribed", "two", "test");
            await Until(() => dispatcher.GetSnapshot().Suppressed == 1);
            handler.Status = HttpStatusCode.ServiceUnavailable;
            dispatcher.EnqueueAlert("test", "three", "test");
            await Until(() => dispatcher.GetSnapshot().Failed == 1);
            repository.Fail = true;
            dispatcher.EnqueueAlert("test", "four", "test");
            await Until(() => dispatcher.GetSnapshot().Failed == 2);
            repository.Fail = false;
            handler.Block = true;
            dispatcher.EnqueueAlert("test", "five", "test");
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher.StopAsync(default);
            var after = dispatcher.GetSnapshot();
            Check(after.Submitted == after.Delivered + after.Failed + after.Suppressed + after.Discarded && after.Discarded == 1,
                "Notification outcomes did not balance.");
            var controller = new Wafish.Dashboard.Modules.Dashboard.Controllers.WebhookSettingsController(
                provider.GetRequiredService<WebhookNotificationService>(), dispatcher, provider,
                NullLogger<Wafish.Dashboard.Modules.Dashboard.Controllers.WebhookSettingsController>.Instance);
            var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)await controller.GetSettings(default);
            var json = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
            Check(json.GetProperty("dispatch").GetProperty("Discarded").GetInt64() == 1, "Administration did not expose queue losses.");
        });

        await run("Correctness: recent errors and indexed ranges compare UTC instants", async () =>
        {
            await using var host = await Fixture.Create();
            var repo = host.App.Services.GetRequiredService<IProxyLogRepository>();
            var now = DateTimeOffset.UtcNow;
            string[] timestamps = [now.AddHours(-11).ToString("O"), now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(14)).ToString("O"),
                now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(-12)).ToString("O"), now.AddDays(-2).ToString("O"), now.AddDays(1).ToString("O")];
            await repo.WriteBatchAsync(timestamps.Select(t => new ProxyLogMetaEntity { Timestamp = t, EventType = "ProxyResponse", StatusCode = 503 }).ToArray(), new ProxyLogBodyEntity?[timestamps.Length]);
            Check(await repo.GetRecent5xxCountAsync(5) == 2, "Recent window included expired or future errors.");
            var range = await repo.SearchAsync(1, 20, startTime: now.AddMinutes(-5).UtcDateTime, endTime: now.UtcDateTime);
            Check(range.TotalCount == 2, "Offset-aware range query failed.");
            await using var conn = host.App.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
            await conn.OpenAsync();
            await using var command = conn.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT Id FROM proxy_logs_meta WHERE julianday(Timestamp) >= julianday(@from)";
            command.Parameters.AddWithValue("@from", now.AddMinutes(-5).ToString("O"));
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var details = new List<string>();
                while (await reader.ReadAsync())
                {
                    details.Add(reader.GetString(3));
                }

                Check(details.Any(d => d.Contains("ix_proxy_logs_meta_utc", StringComparison.Ordinal)), "Time range lost its index.");
            }
            await repo.CleanupAsync(1, 1);
            Check((await repo.SearchAsync(1, 20)).TotalCount == 4, "Retention compared timestamp text instead of UTC.");
            // Reapply the new migration to an existing database containing legacy offset strings.
            await host.Sql("DROP INDEX ix_proxy_logs_meta_utc; DELETE FROM schema_migrations WHERE id = '020_proxy_log_utc_index';");
            await ActivatorUtilities.CreateInstance<SqliteSchemaMigrator>(host.App.Services).StartAsync(default);
            Check(await repo.GetRecent5xxCountAsync(5) == 2, "Migration changed existing-row time semantics.");
            foreach (var instant in new[] { new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero) })
            {
                var before = instant.AddMinutes(-6).ToOffset(TimeSpan.FromHours(2)).ToString("O");
                var inside = instant.AddMinutes(-1).ToOffset(TimeSpan.FromHours(1)).ToString("O");
                await repo.WriteBatchAsync([new() { Timestamp = before, StatusCode = 503 }, new() { Timestamp = inside, StatusCode = 503 }], [null, null]);
                Check((await repo.SearchAsync(1, 20, startTime: instant.AddMinutes(-5).UtcDateTime, endTime: instant.UtcDateTime)).TotalCount == 1,
                    "Midnight or daylight-saving offset changed range membership.");
            }
        });

        await run("Correctness: batch route deletion removes only final orphans and rolls back on failure", async () =>
        {
            await using var host = await Fixture.Create();
            var cluster = new ClusterConfig { ClusterId = "shared", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://localhost:9000/" } } };
            var routes = Enumerable.Range(1, 3).Select(i => new RouteConfig { RouteId = "r" + i, ClusterId = "shared", Match = new() { Path = "/r" + i } }).ToArray();
            Check((await host.Config.ImportBatchAsync([cluster], routes)).Success, "Setup failed.");
            await host.Config.BatchDeleteRoutesAsync(["r1", "r2"], true);
            Check(host.Config.GetClusters().Count == 1 && host.Config.GetRoutes().Single().RouteId == "r3", "Deletion removed a referenced cluster.");
            await host.Config.TryAddRouteConfig(routes[0]);
            await host.Sql("CREATE TRIGGER reject_delete BEFORE DELETE ON yarp_clusters BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            var failed = await host.Config.BatchDeleteRoutesAsync(["r1", "r3"], true);
            Check(failed.Succeeded == 0 && host.Config.GetRoutes().Count == 2 && host.Config.GetClusters().Count == 1, "Failed deletion changed active state.");
            await host.Sql("DROP TRIGGER reject_delete;");
            await host.Config.BatchDeleteRoutesAsync(["r1", "r3"], true);
            Check(host.Config.GetRoutes().Count == 0 && host.Config.GetClusters().Count == 0, "Final shared cluster remained orphaned.");
        });

        await run("Metrics: successful, throwing, and cancelled attempts finalize once", async () =>
        {
            var route = new RouteConfig { RouteId = "metrics", Match = new() { Path = "/metrics" } };
            var publisher = new GatewaySnapshotPublisher();
            publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route], RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("metrics",
                [new("metrics", "traffic-metrics", PluginBindingScope.Route, "metrics", "{\"enabled\":true}", 1, 1, 0)]) });
            var plans = new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            var metrics = new PluginMetricStore(); var statistics = new LockFreeStatistics();
            foreach (var mode in new[] { "success", "status", "exception", "cancellation", "aborted" })
            {
                var context = new DefaultHttpContext();
                context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
                context.Request.ContentLength = 7; context.Response.ContentLength = 11;
                var middleware = new PluginMetricsMiddleware(c =>
                {
                    if (mode == "exception")
                    {
                        throw new IOException("injected");
                    }

                    if (mode == "cancellation")
                    {
                        throw new OperationCanceledException();
                    }

                    if (mode == "aborted")
                    {
                        c.RequestAborted = new CancellationToken(true);
                    }

                    c.Response.StatusCode = mode == "status" ? 503 : 200;
                    return Task.CompletedTask;
                }, plans, statistics, metrics);
                try { await middleware.InvokeAsync(context); }
                catch (IOException) when (mode == "exception") { }
                catch (OperationCanceledException) when (mode == "cancellation") { }
                Check(context.Response.StatusCode == (mode == "status" ? 503 : 200), "Measurement changed the response.");
            }
            var snapshot = metrics.GetRoutes()["metrics"];
            Check(snapshot.Requests == 5 && snapshot.Errors == 2 && snapshot.Exceptions == 1 && snapshot.Cancellations == 2,
                "Attempt outcomes were missing or duplicated.");
            Check(snapshot.RequestBytes == 35 && snapshot.ResponseBytes == 55 && statistics.GetSnapshot().TotalRequests == 5, "Byte or global accounting changed.");
            await CheckLiveMetricsAsync();
        });
    }

    private static async Task CheckLiveMetricsAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        await using var upstream = builder.Build(); upstream.Urls.Add("http://127.0.0.1:0");
        var calls = 0;
        upstream.MapGet("/metrics", (HttpContext c) => { c.Response.StatusCode = Interlocked.Increment(ref calls) <= 2 ? 503 : 200; });
        await upstream.StartAsync();
        await using var host = await Fixture.Create();
        var address = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await host.Config.ImportBatchAsync([new ClusterConfig { ClusterId = "shared", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = address } } }],
            [new RouteConfig { RouteId = "metrics", ClusterId = "shared", Match = new() { Path = "/metrics" } }]);
        var state = host.Config.GetDynamicConfig()!;
        var mutations = host.App.Services.GetRequiredService<IPluginBindingMutationService>();
        foreach (var (id, json) in new[] { ("request-retry", "{\"enabled\":true,\"maxRetries\":2,\"backoffBaseMs\":1,\"backoffJitterMs\":0}"), ("traffic-metrics", "{\"enabled\":true}") })
        {
            await mutations.UpsertAsync(new() { Id = id, PluginId = id, PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "metrics", RouteUid = state.Routes.Single().RouteUid, ConfigJson = json });
        }

        await mutations.UpsertAsync(new() { Id = "cluster-metrics", PluginId = "cluster-metrics", PluginVersion = "1.0", Scope = PluginBindingScope.Cluster, ScopeId = "shared", ClusterUid = state.Clusters.Single().ClusterUid, ConfigJson = "{\"enabled\":true}" });
        host.App.Urls.Add("http://127.0.0.1:0"); host.App.UseRouting(); host.App.MapWafIshProxy();
        await host.App.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            using var response = await client.GetAsync("/metrics");
            Check(response.IsSuccessStatusCode, "Retry did not recover.");
            var store = host.App.Services.GetRequiredService<PluginMetricStore>();
            Check(store.GetRoutes()["metrics"].Requests == 3 && store.GetClusters()["shared"].Requests == 3 && store.GetDestinations()["shared/one"].Requests == 3,
                "Both plugins duplicated counts or retries were not counted as attempts.");
            await mutations.DeleteAsync("traffic-metrics");
            Check(host.App.Services.GetRequiredService<IGatewayPluginManager>().SetPluginEnabled("traffic-metrics", false).Succeeded, "Traffic plugin could not be disabled.");
            await mutations.ReconcileAsync();
            using var clusterOnly = await client.GetAsync("/metrics");
            Check(clusterOnly.IsSuccessStatusCode && store.GetClusters()["shared"].Requests == 4 && store.GetRoutes()["metrics"].Requests == 3,
                "Cluster-only metrics required the traffic plugin.");
        }
        finally { await host.App.StopAsync(); await upstream.StopAsync(); }
    }

    public class LogRepositoryProxy : DispatchProxy
    {
        public IProxyLogRepository Inner { get; set; } = null!;
        public Func<CancellationToken, Task> BeforeWrite { get; set; } = _ => Task.CompletedTask;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IProxyLogRepository.WriteBatchAsync))
            {
                return method.Invoke(Inner, args);
            }

            return WriteAsync(args!);
        }
        private async Task WriteAsync(object?[] args)
        {
            var ct = (CancellationToken)args[2]!;
            await BeforeWrite(ct);
            await Inner.WriteBatchAsync((IReadOnlyList<ProxyLogMetaEntity>)args[0]!, (IReadOnlyList<ProxyLogBodyEntity?>)args[1]!, ct);
        }
    }
    private sealed class NotificationSettings(WebhookSettingsData data) : IWebhookSettingsRepository
    {
        public bool Fail { get; set; }
        public Task<WebhookSettingsData> LoadAsync(CancellationToken ct = default) => Fail ? throw new IOException("Injected settings failure") : Task.FromResult(data);
        public Task SaveAsync(WebhookSettingsData settings, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class TestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class NotificationHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return new(Status) { Content = new StringContent("ok") };
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!ready())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
}
