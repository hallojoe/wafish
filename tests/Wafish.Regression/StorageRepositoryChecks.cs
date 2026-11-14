using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Wafish.Storage.Abstractions.Storage.Entities;
using Wafish.Storage.Sqlite;

internal static class StorageRepositoryChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Logging: persistence starts once, writes batches, and stops without losing entries", async () =>
        {
            await using var db = new Database();
            var settings = new Wafish.Plugin.ProxyLog.Services.ProxyLogRuntimeSettings(Microsoft.Extensions.Options.Options.Create(new Wafish.Plugin.ProxyLog.ProxyLogPluginOptions()));
            var store = new Wafish.Plugin.ProxyLog.Services.ProxyLogStore(runtimeSettings: settings);
            var writer = new Wafish.Plugin.ProxyLog.Services.SqliteProxyLogWriter(db.Get<IProxyLogRepository>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Wafish.Plugin.ProxyLog.Services.SqliteProxyLogWriter>.Instance);
            var logger = new EnabledLogger<Wafish.Plugin.ProxyLog.Services.AsyncLogPersistenceService>();
            var service = new Wafish.Plugin.ProxyLog.Services.AsyncLogPersistenceService(store, writer, settings, null!, logger);
            for (var i = 0; i < 100; i++)
            {
                store.Add(new() { Timestamp = DateTime.Now, EventType = Wafish.Plugin.ProxyLog.Models.LogEventType.ProxyResponse, Level = "Information", RouteId = "lifecycle", StatusCode = 200 });
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await service.StartResourceAsync(timeout.Token);
                await service.StartResourceAsync(timeout.Token);
                while (service.WrittenCount < 100)
                {
                    await Task.Delay(10, timeout.Token);
                }
            }
            finally { await service.StopResourceAsync(timeout.Token); }
            await service.StopResourceAsync(timeout.Token);
            Check((await db.Get<IProxyLogRepository>().SearchAsync(1, 200, routeId: "lifecycle")).TotalCount == 100, "Persistence lost or duplicated a batch.");
            Check(logger.Messages.Count(m => m.Contains("starting:", StringComparison.Ordinal)) == 1, "Idempotent start created multiple consumers.");
            Check(logger.Messages.Any(m => m.Contains("stopped", StringComparison.Ordinal)), "Shutdown did not complete.");
        });

        await run("Notifications: settings round-trip and test responses redact URL secrets", async () =>
        {
            await using var db = new Database();
            using var http = new WebhookClientFactory();
            var webhooks = new Wafish.Dashboard.Infrastructure.Notifications.WebhookNotificationService(http, db.Services,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Wafish.Dashboard.Infrastructure.Notifications.WebhookNotificationService>.Instance);
            var logger = new EnabledLogger<Wafish.Dashboard.Modules.Dashboard.Controllers.WebhookSettingsController>();
            var controller = new Wafish.Dashboard.Modules.Dashboard.Controllers.WebhookSettingsController(webhooks, null!, db.Services, logger);
            await controller.SaveSettings(new() { EnabledEvents = ["Test", "Test"], RetryCount = 0, TimeoutSeconds = 3 }, default);
            const string url = "https://webhook.test/hook?access_token=private-one&token=private-two&key=private-three&secret=private-four&visible=yes";
            await controller.SavePlatform(new() { Platform = "generic", Endpoints = [new() { Url = url }, new() { Url = url }] }, default);
            var settings = await webhooks.GetSettingsAsync();
            Check(settings.Platforms["generic"].Count == 1 && settings.EnabledEvents.Count == 1, "Settings did not deduplicate entries.");
            var action = (Microsoft.AspNetCore.Mvc.OkObjectResult)await controller.Test(new() { Platform = "generic" }, default);
            var response = System.Text.Json.JsonSerializer.Serialize(action.Value);
            Check(response.Contains("visible=yes", StringComparison.Ordinal) && !response.Contains("private-", StringComparison.Ordinal), "Test response exposed endpoint credentials.");
            Check(http.LastUri?.Query.Contains("private-one", StringComparison.Ordinal) == true, "Redaction changed the delivery URL.");
            Check(logger.Messages.Count == 2, "Enabled settings diagnostics were lost.");
        });

        await run("Storage: route and cluster repositories preserve values, isolate deletes, and roll back batches", async () =>
        {
            await using var db = new Database();
            var routes = db.Get<IRouteRepository>();
            var clusters = db.Get<IClusterRepository>();
            await clusters.SaveClusterAsync(new() { ClusterId = "quoted'cluster", ClusterUid = "uid-one", LoadBalancingPolicy = "RoundRobin" });
            await clusters.SaveClustersAsync([new() { ClusterId = "other", ClusterUid = "uid-two" }]);
            Check((await clusters.GetClusterAsync("quoted'cluster"))?.ClusterUid == "uid-one", "Cluster identity changed.");
            Check(await clusters.GetClusterAsync("' OR 1=1 --") is null, "Cluster lookup accepted SQL injection.");
            await clusters.SaveDestinationsAsync("quoted'cluster", [new() { DestinationId = "d", Address = "http://localhost:8080/", Health = "http://localhost:8080/health", Host = "example.test", Healthy = false, Metadata = "{}" }]);
            var destination = (await clusters.GetDestinationsAsync("quoted'cluster")).Single();
            Check(destination.Host == "example.test" && !destination.Healthy && destination.Health!.EndsWith("/health"), "Destination values did not round trip.");
            await routes.SaveRouteAsync(new() { RouteId = "route'one", RouteUid = "r-one", ClusterId = "quoted'cluster", ClusterUid = "uid-one", MatchPath = "/one", Enabled = false });
            await routes.SaveRoutesAsync([new() { RouteId = "two", ClusterId = "other", MatchPath = "/two" }, new() { RouteId = "three", ClusterId = "other", MatchPath = "/three" }]);
            Check((await routes.GetRouteAsync("route'one")) is { Enabled: false, RouteUid: "r-one" }, "Route state changed.");
            Check(await routes.GetRouteAsync("' OR 1=1 --") is null, "Route lookup accepted SQL injection.");
            Check((await routes.GetRoutesByClusterAsync("other")).Count == 2, "Cluster filter lost routes.");
            var rejected = false;
            try
            {
                await routes.SaveRoutesAsync([new() { RouteId = "rollback", ClusterId = "other" }, new() { RouteId = null!, ClusterId = "other" }]);
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException) { rejected = true; }
            Check(rejected, "Invalid batch was accepted.");
            Check(await routes.GetRouteAsync("rollback") is null, "Failed batch left partial rows.");
            await routes.DeleteRouteAsync("two");
            await routes.DeleteRoutesByClusterAsync("quoted'cluster");
            Check((await routes.GetAllRoutesAsync()).Single().RouteId == "three", "Delete crossed cluster boundaries.");
            await clusters.DeleteDestinationsAsync("quoted'cluster");
            Check((await clusters.GetDestinationsAsync("quoted'cluster")).Count == 0, "Destination delete failed.");
            await clusters.DeleteClusterAsync("quoted'cluster");
            Check((await clusters.GetAllClustersAsync()).Single().ClusterId == "other", "Cluster delete removed a peer.");
        });

        await run("Storage: binding scope filters accept stable identities and reject SQL injection", async () =>
        {
            await using var db = new Database();
            var repo = db.Get<IPluginConfigurationRepository>();
            await repo.UpsertBindingAsync(new() { Id = "route-binding", PluginId = "test", PluginVersion = "1", Scope = PluginBindingScope.Route, ScopeId = "r'one", RouteUid = "route-uid", ConfigJson = "{}" });
            await repo.UpsertBindingAsync(new() { Id = "cluster-binding", PluginId = "test", PluginVersion = "1", Scope = PluginBindingScope.Cluster, ScopeId = "cluster-one", ClusterUid = "cluster-uid", ConfigJson = "{}" });
            Check((await repo.GetBindingsAsync(PluginBindingScope.Route, "r'one")).Single().Id == "route-binding", "Quoted scope lookup failed.");
            Check((await repo.GetBindingsAsync(PluginBindingScope.Route, "route-uid")).Single().Id == "route-binding", "Stable route identity lookup failed.");
            Check((await repo.GetBindingsAsync(PluginBindingScope.Cluster, "cluster-uid")).Single().Id == "cluster-binding", "Stable cluster identity lookup failed.");
            Check((await repo.GetBindingsAsync(PluginBindingScope.Route, "' OR 1=1 --")).Count == 0, "Scope accepted SQL injection.");
            Check((await repo.GetBindingsAsync(PluginBindingScope.Cluster, "route-uid")).Count == 0, "Binding crossed scopes.");
        });

        await run("Storage: runtime state opens connections, isolates nodes, updates, and deletes", async () =>
        {
            await using var db = new Database();
            var repo = db.Get<IPluginRuntimeStateRepository>();
            Check(await repo.GetAsync("circuit", "route", "r", "n1") is null, "Missing state must return null.");
            var entry = new PluginRuntimeStateEntity { PluginId = "circuit", TargetType = "route", TargetUid = "r", NodeId = "n1", StateJson = "{\"open\":true}" };
            await repo.UpsertAsync(entry);
            entry.StateJson = "{\"open\":false}";
            await repo.UpsertAsync(entry);
            await repo.UpsertAsync(new() { PluginId = "circuit", TargetType = "route", TargetUid = "r", NodeId = "n2" });
            await repo.UpsertAsync(new() { PluginId = "other", TargetType = "route", TargetUid = "r", NodeId = "n1" });
            Check((await repo.GetAsync("circuit", "route", "r", "n1"))?.StateJson == entry.StateJson, "Upsert lost state.");
            Check((await repo.GetAllByPluginAsync("circuit")).Count == 2, "Plugin filter leaked state.");
            Check((await repo.GetByTargetAsync("circuit", "route", "r")).Count == 2, "Target filter lost a node.");
            Check(await repo.DeleteAsync("circuit", "route", "r", "n1"), "Delete failed.");
            Check(!await repo.DeleteAsync("circuit", "route", "r", "n1"), "Repeated delete must report false.");
            Check(await repo.DeleteAllByPluginAsync("circuit") == 1, "Delete-all count is wrong.");
            Check((await repo.GetAllByPluginAsync("other")).Count == 1, "Delete crossed plugin boundaries.");
        });

        await run("Storage: cancelled initialization does not create a database", async () =>
        {
            await using var db = new Database();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await db.Get<SqliteConnectionFactory>().CreateConnectionAsync(cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Check(!File.Exists(db.Path), "Cancelled initialization created a database.");
            await using var connection = await db.Get<SqliteConnectionFactory>().CreateConnectionAsync();
            await connection.OpenAsync();
            Check(connection.State == System.Data.ConnectionState.Open, "Cancellation poisoned later initialization.");
        });

        await run("Storage: log settings upsert atomically and clear", async () =>
        {
            await using var db = new Database();
            var repo = db.Get<ILogSettingsRepository>();
            Check((await repo.LoadAllAsync()).Count == 0, "New settings should be empty.");
            await repo.SaveAsync("quoted'key", "quoted'value");
            await repo.SaveBatchAsync([("quoted'key", "updated"), ("second", "2")]);
            var settings = await repo.LoadAllAsync();
            Check(settings.Count == 2 && settings["quoted'key"] == "updated", "Settings did not round-trip.");
            await repo.ClearAsync();
            Check((await repo.LoadAllAsync()).Count == 0, "Clear retained settings.");
        });

        await run("Storage: AI results preserve content, filter severity, and enforce retention", async () =>
        {
            await using var db = new Database();
            var repo = db.Get<IAIAnalysisRepository>();
            await repo.SaveAnalysisAsync(new() { AnalysisType = "summary", Content = "old", Severity = 1, CreatedAt = DateTime.Now.AddDays(-10) });
            await repo.SaveAnalysisAsync(new() { AnalysisType = "anomaly", Content = "quoted'content", Severity = 3, RelatedRoutes = "[\"r\"]", RelatedClusters = "[\"c\"]" });
            Check((await repo.GetRecentAsync()).Count == 2, "Recent results lost entries.");
            var filtered = await repo.GetRecentAsync(20, "anomaly");
            Check(filtered.Count == 1 && filtered[0].Content == "quoted'content" && filtered[0].RelatedRoutes == "[\"r\"]", "Analysis filter or mapping failed.");
            Check((await repo.GetRecentAsync(20, "' OR 1=1 --")).Count == 0, "Analysis filter accepted SQL injection.");
            Check((await repo.GetBySeverityAsync(2)).Single().Id == filtered[0].Id, "Severity filter failed.");
            await repo.PurgeOlderThanAsync(DateTime.Now.AddDays(-1));
            Check((await repo.GetRecentAsync()).Count == 1, "Retention deleted recent analysis.");
            await repo.DeleteByIdAsync(filtered[0].Id);
            Check((await repo.GetRecentAsync()).Count == 0, "Analysis delete failed.");
        });

        await run("Storage: conversations retain chronological order and isolate sessions", async () =>
        {
            await using var db = new Database();
            var repo = db.Get<IAIConversationRepository>();
            await repo.SaveMessageAsync(new() { SessionId = "s'1", Role = "user", Content = "old", CreatedAt = DateTime.Now.AddDays(-2) });
            await repo.SaveMessageAsync(new() { SessionId = "s'1", Role = "assistant", Content = "reply", FunctionCalls = "[]", ToolCallId = "tool" });
            await repo.SaveMessageAsync(new() { SessionId = "s2", Content = "isolated" });
            var messages = await repo.GetSessionMessagesAsync("s'1");
            Check(messages.Count == 2 && messages[0].Content == "old" && messages[1].ToolCallId == "tool", "Conversation ordering or mapping failed.");
            Check((await repo.GetSessionMessagesAsync("s'1", 1)).Single().Content == "reply", "Message limit must retain the latest message.");
            Check((await repo.ListSessionsAsync()).Count == 2, "Session summary lost a session.");
            await repo.PurgeOlderThanAsync(DateTime.Now.AddDays(-1));
            Check((await repo.GetSessionMessagesAsync("s'1")).Count == 1, "Conversation retention failed.");
            await repo.DeleteSessionAsync("s'1");
            Check((await repo.ListSessionsAsync()).Single().SessionId == "s2", "Delete crossed session boundaries.");
        });

        await run("Storage: webhook settings and bounded delivery history round-trip", async () =>
        {
            await using var db = new Database();
            var settings = db.Get<IWebhookSettingsRepository>();
            Check((await settings.LoadAsync()).Platforms.Count == 0, "New webhook settings should be empty.");
            await settings.SaveAsync(new() { Platforms = new() { ["custom"] = [new() { Url = "https://example.invalid/hook", Secret = "local-secret" }] }, EnabledEvents = ["created"], PlatformEvents = new() { ["custom"] = ["deleted"] }, CooldownSeconds = 3, RetryCount = 1, TimeoutSeconds = 5 });
            var loaded = await settings.LoadAsync();
            Check(loaded.Platforms["custom"][0].Secret == "local-secret" && loaded.PlatformEvents["custom"].Single() == "deleted" && loaded.TimeoutSeconds == 5, "Webhook settings mapping failed.");
            var history = db.Get<IWebhookDeliveryRecordRepository>();
            for (var i = 0; i < 3; i++)
            {
                await history.AddAsync(new() { Platform = "custom", EventType = "created", Target = i.ToString(), Success = i != 1, HttpStatusCode = i == 1 ? 500 : 200, Error = i == 1 ? "failed" : null, DurationMs = 10, CreatedAt = DateTime.Now.AddSeconds(i) }, maxRecords: 2);
            }

            var recent = await history.GetRecentAsync();
            Check(recent.Count == 2 && recent[0].Target == "2" && recent[1].Error == "failed", "Delivery retention or mapping failed.");
            await history.ClearAsync();
            Check((await history.GetRecentAsync()).Count == 0, "Delivery clear failed.");
        });

        await run("Storage: configuration history and audit filters preserve quoted values", async () =>
        {
            await using var db = new Database();
            var history = db.Get<IConfigHistoryRepository>();
            for (var i = 0; i < 3; i++)
            {
                await history.SaveConfigHistoryAsync(new() { VersionId = "v" + i, Description = "revision'" + i, CreatedBy = "tester", ClientIp = "127.0.0.1", ConfigData = "{}", DiffData = "[]", CreatedAt = DateTime.Now.AddSeconds(i) });
            }

            Check((await history.GetConfigHistoryAsync("v1"))?.Description == "revision'1", "History values changed.");
            Check(await history.GetConfigHistoryAsync("missing") is null, "Missing version should return null.");
            await history.DeleteOldConfigHistoryAsync(2);
            Check((await history.GetConfigHistoryListAsync()).Count == 2, "History retention failed.");
            await history.DeleteConfigHistoryAsync("v1");
            Check((await history.GetConfigHistoryListAsync()).Single().VersionId == "v2", "Wrong version deleted.");
            await history.ClearConfigHistoryAsync();
            Check((await history.GetConfigHistoryListAsync()).Count == 0, "History clear failed.");
            var audits = db.Get<IAuditLogRepository>();
            for (var i = 0; i < 3; i++)
            {
                await audits.SaveAuditLogAsync(new() { Id = "a" + i, Action = "update", Target = i == 0 ? "other" : "quoted'target", TargetType = "route", TargetUid = "uid", Operator = "tester", BeforeData = "{}", AfterData = "{}", Success = i != 1, ErrorMessage = i == 1 ? "failed" : null, Timestamp = DateTime.Now.AddSeconds(i) });
            }

            Check((await audits.GetAuditLogsByTargetAsync("quoted'target")).Count == 2, "Audit target filter failed.");
            await audits.DeleteOldAuditLogsAsync(1);
            Check((await audits.GetAuditLogsAsync()).Single().Id == "a2", "Audit retention deleted the wrong row.");
        });

        await run("Storage: plugin schemas and presets update and delete without crossing plugin boundaries", async () =>
        {
            await using var db = new Database();
            var repo = db.Get<IPluginConfigurationRepository>();
            Check(await repo.GetSchemaAsync("cache", 1) is null, "Missing schema should return null.");
            await repo.UpsertSchemaAsync(new() { PluginId = "cache", SchemaVersion = 1, SchemaJson = "{\"type\":\"object\"}" });
            Check((await repo.GetSchemasAsync()).Single().SchemaVersion == 1, "Schema enumeration failed.");
            Check((await repo.GetSchemaAsync("cache", 1))?.SchemaJson.Contains("object") == true, "Schema content changed.");
            var preset = new PluginConfigPresetEntity { Id = "preset", Name = "quoted'name", PluginId = "cache", Description = "description", ConfigJson = "{\"ttl\":1}" };
            await repo.UpsertPresetAsync(preset);
            preset.ConfigJson = "{\"ttl\":2}";
            await repo.UpsertPresetAsync(preset);
            Check((await repo.GetPresetAsync("preset"))?.ConfigJson == preset.ConfigJson, "Preset update failed.");
            Check((await repo.GetPresetsByPluginAsync("other")).Count == 0, "Preset filter leaked another plugin.");
            Check((await repo.GetPresetsAsync()).Single().Name == "quoted'name", "Preset name changed.");
            Check(await repo.DeletePresetAsync("preset") && !await repo.DeletePresetAsync("preset"), "Preset delete result is wrong.");
            Check(await repo.GetPresetAsync("preset") is null, "Deleted preset remained visible.");
        });

        await run("Storage: proxy logs preserve bodies, parameterize filters, aggregate, and expire", async () =>
        {
            await using var db = new Database();
            var repo = db.Get<IProxyLogRepository>();
            var now = DateTime.Now;
            var meta = Enumerable.Range(0, 3).Select(i => new ProxyLogMetaEntity { Timestamp = now.AddSeconds(-i).ToString("O"), EventType = "ProxyResponse", Level = "Information", RouteId = "r'1", ClusterId = "c", Method = "GET", UpstreamPath = "/hello", StatusCode = i == 2 ? 503 : 200, ElapsedMs = 10 + i, TraceId = "trace" + i, HasResponseBody = 1 }).ToArray();
            await repo.WriteBatchAsync(meta, [new() { ResponseBody = "one" }, new() { ResponseBody = "two", RequestHeaders = "{}" }, null]);
            var page = await repo.SearchAsync(1, 10, routeId: "r'1", clusterId: "c", level: "Information", statusCodeMin: 200, statusCodeMax: 599, startTime: now.AddMinutes(-1), endTime: now.AddMinutes(1), keyword: "hello", eventType: "ProxyResponse");
            Check(page.TotalCount == 3 && page.Items.Count == 3, "Proxy-log filter lost rows.");
            Check((await repo.SearchAsync(1, 10, routeId: "' OR 1=1 --")).TotalCount == 0, "Log search accepted SQL injection.");
            Check((await repo.SearchAsync(2, 2)).Items.Count == 1, "Log pagination failed.");
            var id = page.Items[0].Id;
            Check((await repo.GetMetaByIdAsync(id))?.TraceId == "trace0", "Log metadata changed.");
            Check((await repo.GetBodyByMetaIdAsync(id))?.ResponseBody == "one", "Log body changed.");
            Check(await repo.GetMetaByIdAsync(long.MaxValue) is null && await repo.GetBodyByMetaIdAsync(long.MaxValue) is null, "Missing logs should return null.");
            var stats = await repo.GetStatsAsync(10);
            Check(stats.TotalRequests == 3 && stats.SuccessCount == 2 && stats.ErrorCount == 1 && stats.P50LatencyMs == 11, "Log aggregation failed.");
            Check((await repo.GetTrafficDataAsync(now.AddMinutes(-1))).Sum(x => x.RequestCount) == 3, "Traffic aggregation failed.");
            Check((await repo.GetTopIssuesAsync(now.AddMinutes(-1), 5)).Single().ErrorCount == 1, "Top error aggregation failed.");
            Check(await repo.GetRecent5xxCountAsync(10) == 1, "Recent error count is wrong.");
            await repo.WriteBatchAsync([new() { Timestamp = now.AddDays(-20).ToString("O"), EventType = "ProxyResponse", Level = "Information" }], [new() { ResponseBody = "expired" }]);
            await repo.CleanupAsync(7, 3);
            Check((await repo.SearchAsync(1, 10)).TotalCount == 3, "Log retention deleted recent rows.");
            await repo.CheckpointAsync();
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class EnabledLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level != Microsoft.Extensions.Logging.LogLevel.None;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, error));
    }

    private sealed class WebhookClientFactory : HttpMessageHandler, IHttpClientFactory
    {
        public Uri? LastUri { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class Database : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wafish-repository-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly ServiceProvider _provider;

        public Database()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Storage:Sqlite:ConnectionString"] = new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()
            }).Build();
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddWafishStorage();
            _provider = services.BuildServiceProvider();
        }

        public IServiceProvider Services => _provider;

        public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(Path + suffix);
            }
        }
    }
}
