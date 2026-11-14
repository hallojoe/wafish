using WafIsh.Models;
using WafIsh.Runtime;
using WafIsh.Services;
using WafIsh.Storage;
using WafIsh.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

internal static class ConfigurationTransactionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Configuration: cluster APIs preserve updates, reject duplicates, and record enabled logs", async () =>
        {
            using var logs = new CapturingLogs();
            await using var host = await Fixture.Create(logs: logs);
            Applied(await host.Config.TryAddCluster("first", new() { ["one"] = "http://localhost:12345/" }, "RoundRobin"));
            Applied(await host.Config.TryAddCluster(new CreateClusterRequest { ClusterId = "second", Destinations = new() { ["two"] = "http://localhost:12346/" } }));
            var duplicate = await host.Config.TryAddCluster(new CreateClusterRequest { ClusterId = "second", Destinations = new() { ["two"] = "http://localhost:12346/" } });
            Assert(!duplicate.Success, "Duplicate creation was accepted.");
            Applied(await host.Config.TryUpdateCluster("second", new() { Destinations = new() { ["three"] = "http://localhost:12347/" }, LoadBalancingPolicy = "Random" }));
            Assert(host.Config.GetClusters().Single(c => c.ClusterId == "second").Destinations!.ContainsKey("three"), "Cluster update lost its new destination.");
            var request = new RegisterRouteRequest { RouteName = "route", ClusterName = "first", MatchPath = "/test/{**rest}", DestinationAddress = "http://localhost:12345/" };
            Applied(await host.Config.TryAddRoute(request));
            request.MatchPath = "/updated/{**rest}";
            Applied(await host.Config.TryAddRoute(request));
            Assert(host.Config.GetRoutes().Single().Match.Path == "/updated/{**rest}", "Route update was lost.");
            Assert(logs.Messages.Any(message => message.Contains("second", StringComparison.Ordinal)) && logs.Messages.Any(message => message.Contains("updated", StringComparison.Ordinal)), "Enabled configuration logs were lost: " + string.Join(" | ", logs.Messages.Take(10)));
        });

        await run("Registration: concurrent instances share routes and preserve peer destinations", async () =>
        {
            await using var host = await Fixture.Create();
            var config = host.Config;
            var results = await Task.WhenAll(config.RegisterInstanceAsync("service", "one", Cluster("shared"), [Route("service", "shared")]),
                config.RegisterInstanceAsync("service", "two", Cluster("shared"), [Route("service", "shared")]));
            foreach (var result in results)
            {
                Applied(result);
            }

            Assert(config.GetRoutes().Count == 1 && config.GetClusters().Single().Destinations!.Count == 2, "Instance registration lost a peer.");
            Applied(await config.RegisterInstanceAsync("service", "one", Cluster("shared") with { Destinations = new Dictionary<string, DestinationConfig> { ["destination"] = new() { Address = "http://127.0.0.1:12346/" } } }, [Route("service", "shared")]));
            Assert(config.GetClusters().Single().Destinations!.Count == 2, "Restart duplicated a destination.");
            Assert(config.GetClusters().Single().Destinations!["one:destination"].Address.EndsWith(":12346/"), "Restart did not update the port.");
            var before = config.GetDynamicConfig()!.Version;
            await host.Sql("CREATE TRIGGER reject_instance BEFORE INSERT ON yarp_destinations BEGIN SELECT RAISE(ABORT, 'injected instance failure'); END;");
            Failed(await config.UnregisterInstanceAsync("service", "one"), ConfigurationUpdateStatus.StorageFailed, before, before);
            Assert(config.IsInstanceRegistered("service", "one"), "Failed removal changed active state.");
            await host.Sql("DROP TRIGGER reject_instance;");
            await using var restarted = await Fixture.Create(host.Directory);
            Assert(restarted.Config.IsInstanceRegistered("service", "one") && restarted.Config.IsInstanceRegistered("service", "two"), "Ownership did not survive restart.");
            Applied(await restarted.Config.UnregisterInstanceAsync("service", "one"));
            Assert(restarted.Config.GetRoutes().Count == 1 && restarted.Config.GetClusters().Single().Destinations!.Count == 1, "Removal deleted peer routes.");
            Applied(await restarted.Config.UnregisterInstanceAsync("service", "two"));
            Assert(restarted.Config.GetRoutes().Count == 0 && restarted.Config.GetClusters().Count == 0, "Final removal left orphaned configuration.");
        });

        await run("Configuration: atomic rollback covers single, batch, import, and replacement writes", async () =>
        {
            await using var host = await Fixture.Create();
            var config = host.Config;
            Applied(await config.ImportBatchAsync([Cluster("one")], [Route("one", "one")]));
            var before = await host.Store.LoadAsync();
            var active = config.GetDynamicConfig()!.Version;
            var audit = host.App.Services.GetRequiredService<IConfigChangeAuditLog>();
            var successes = audit.GetRecent(1000).Count(a => a.Success);
            await host.Sql("CREATE TRIGGER reject_destination BEFORE INSERT ON yarp_destinations BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;");
            Failed(await config.TryAddClusterConfig(Cluster("two")), ConfigurationUpdateStatus.StorageFailed, active, active);
            var batch = await config.BatchSetRoutesEnabledAsync(["one"], false);
            Assert(batch.Succeeded == 0 && batch.Status == ConfigurationUpdateStatus.StorageFailed, "Batch reported uncommitted success.");
            Failed(await config.ImportBatchAsync([Cluster("two")], [Route("two", "two")]), ConfigurationUpdateStatus.StorageFailed, active, active);
            try
            {
                await config.ReplaceAllConfig([Route("three", "three")], [Cluster("three")]);
                throw new Exception("Replacement accepted a failed write.");
            }
            catch (ConfigurationUpdateException ex) { Assert(ex.Result.Status == ConfigurationUpdateStatus.StorageFailed, "Wrong replacement failure."); }
            var grpc = new GatewayRegistryGrpcService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayRegistryGrpcService>.Instance);
            var registration = new WafIsh.GatewayRegistry.RegisterServiceRequest { ServiceId = "grpc-write", ServiceName = "grpc-cluster" };
            registration.Paths.AddRange(["/first", "/second"]);
            registration.Destinations.Add(new WafIsh.GatewayRegistry.Destination { DestinationId = "one", Address = "http://127.0.0.1:9/" });
            var grpcResult = await grpc.RegisterService(registration, null!);
            Assert(!grpcResult.Success && grpcResult.Status == "StorageFailed" && grpcResult.RouteIds.Count == 0,
                "gRPC registration reported success after a failed transaction.");
            var after = await host.Store.LoadAsync();
            Assert(after.Version == before.Version && after.Routes.Single().RouteId == "one" && after.Clusters.Single().ClusterId == "one", "Transaction did not roll back.");
            Assert(config.GetDynamicConfig()!.Routes.Single().Enabled && config.GetDynamicConfig()!.Version == active, "Active state changed after failure.");
            Assert(audit.GetRecent(1000).Count(a => a.Success) == successes, "Success audit preceded commit.");
            await host.Sql("DROP TRIGGER reject_destination;");
            Applied(await config.TryAddClusterConfig(Cluster("two")));
            await using var restarted = await Fixture.Create(host.Directory);
            Assert(restarted.Config.GetClusters().Count == 2, "Successful writes did not survive restart.");
        });

        await run("Configuration: cluster rename commits route references and binding identities together", async () =>
        {
            await using var host = await Fixture.Create();
            Applied(await host.Config.ImportBatchAsync([Cluster("one")], [Route("one", "one")]));
            var plugins = host.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var uid = host.Config.GetDynamicConfig()!.Clusters.Single().ClusterUid;
            await plugins.UpsertBindingAsync(new WafIsh.Storage.Entities.PluginBindingEntity
            {
                PluginId = "test-identity", PluginVersion = "1.0", Scope = WafIsh.Storage.Entities.PluginBindingScope.Cluster,
                ScopeId = "one", ClusterUid = uid, ConfigJson = "{}"
            });
            Applied(await host.Config.TryRenameCluster("one", "renamed", new() { ["destination"] = "http://127.0.0.1:12345/" }));
            var stored = await host.Store.LoadAsync();
            Assert(stored.Clusters.Single().ClusterUid == uid && stored.Routes.Single().ClusterId == "renamed", "Rename lost identity or references.");
            Assert((await plugins.GetBindingsAsync()).Single().ScopeId == "renamed", "Binding key was not committed with the rename.");
            Assert(host.Publisher.Current.ClusterPlugins.ContainsKey("renamed"), "Prepared snapshot omitted the renamed binding.");
        });

        await run("Configuration: route rename preserves disabled state and native match fields", async () =>
        {
            await using var host = await Fixture.Create();
            var route = Route("one", "one") with
            {
                Match = new RouteMatch { Path = "/native/{**catch-all}", Hosts = ["example.test"], Methods = ["GET"] },
                Metadata = new Dictionary<string, string> { ["custom"] = "retained" },
                Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = "/native" }]
            };
            Applied(await host.Config.ImportBatchAsync([Cluster("one")], [route]));
            Applied(await host.Config.TrySetRouteEnabled("one", false));
            var uid = host.Config.GetDynamicConfig()!.Routes.Single().RouteUid;
            Applied(await host.Config.TryRenameRoute("one", "renamed", new RegisterRouteRequest { RouteName = "renamed" }));
            await using var restarted = await Fixture.Create(host.Directory);
            var renamed = restarted.Config.GetDynamicConfig()!.Routes.Single();
            Assert(!renamed.Enabled && renamed.RouteUid == uid && renamed.Config.ClusterId == "one", "Rename changed activation or identity.");
            Assert(renamed.Config.Match.Path == route.Match.Path && renamed.Config.Match.Hosts!.Single() == "example.test" && renamed.Config.Match.Methods!.Single() == "GET", "Rename lost native match fields.");
            Assert(renamed.Config.Metadata!["custom"] == "retained" && renamed.Config.Transforms!.Count == 1, "Rename lost advanced configuration.");
        });

        await run("Configuration: validation and stale revisions cannot change stored or active state", async () =>
        {
            await using var host = await Fixture.Create();
            Applied(await host.Config.ImportBatchAsync([Cluster("one")], [Route("one", "one")]));
            var active = host.Config.GetDynamicConfig()!.Version;
            Failed(await host.Config.TryAddRouteConfig(Route("invalid", "missing")), ConfigurationUpdateStatus.ValidationRejected, active, active);
            Failed(await host.Config.TryAddRouteConfig(Route("invalid", "one") with { Match = new RouteMatch { Path = "/{broken" } }), ConfigurationUpdateStatus.ValidationRejected, active, active);
            var stored = await host.Store.LoadAsync();
            await host.Store.SaveAsync(stored with { Version = stored.Version + 1 }, stored.Version);
            Failed(await host.Config.TryAddClusterConfig(Cluster("stale")), ConfigurationUpdateStatus.VersionConflict, active, active + 1);
            Assert(host.Config.GetClusters().Count == 1, "Stale writer changed active state.");
        });

        await run("Configuration: readers see active state while a candidate waits for persistence", async () =>
        {
            await using var host = await Fixture.Create();
            Applied(await host.Config.ImportBatchAsync([Cluster("one")], [Route("one", "one")]));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Store.BeforeSave = async () => { entered.SetResult(); await release.Task; };
            var mutation = host.Config.TrySetRouteEnabled("one", false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var read = host.Config.GetDynamicConfig()!;
                Assert(read.Routes.Single().Enabled, "Reader saw an uncommitted candidate.");
                read.Routes.Clear();
                Assert(host.Config.GetDynamicConfig()!.Routes.Count == 1, "Reader could mutate active state.");
            }
            finally { release.SetResult(); }
            Applied(await mutation);
            Assert(!host.Config.GetDynamicConfig()!.Routes.Single().Enabled, "Committed candidate did not activate.");
        });

        await run("Configuration: activation failure reports both revisions and recovers after restart", async () =>
        {
            await using var host = await Fixture.Create();
            var active = host.Config.GetDynamicConfig()!.Version;
            host.Publisher.Fail = true;
            Failed(await host.Config.TryAddClusterConfig(Cluster("saved")), ConfigurationUpdateStatus.ActivationFailed, active, active + 1);
            Assert((await host.Store.LoadAsync()).Clusters.Single().ClusterId == "saved", "Activation failure lost the stored revision.");
            Failed(await host.Config.TryAddClusterConfig(Cluster("blocked")), ConfigurationUpdateStatus.ActivationFailed, active, active + 1);
            await using var restarted = await Fixture.Create(host.Directory);
            Assert(restarted.Config.GetClusters().Single().ClusterId == "saved", "Restart did not activate stored configuration.");
        });
    }

    private static ClusterConfig Cluster(string id) => new()
    {
        ClusterId = id, Destinations = new Dictionary<string, DestinationConfig>
        { ["destination"] = new() { Address = "http://127.0.0.1:12345/", Health = "http://127.0.0.1:12345/health" } }
    };
    private static RouteConfig Route(string id, string cluster) => new() { RouteId = id, ClusterId = cluster, Match = new() { Path = "/" + id } };
    private static void Assert(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
    private static void Applied(RouteOperationResult result) => Assert(result.Success && result.Status == ConfigurationUpdateStatus.Applied, result.Message);
    private static void Failed(RouteOperationResult result, ConfigurationUpdateStatus status, long active, long stored)
        => Assert(!result.Success && result.Status == status && result.ActiveVersion == active && result.PersistedVersion == stored, $"Unexpected result: {result}");

    private sealed class CapturingLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public bool IsEnabled(LogLevel level) => level != LogLevel.None;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => messages.Add(format(state, error));
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string Directory { get; init; }
        private bool OwnsDirectory { get; init; }
        public required BlockingStore Store { get; init; }
        public required FailingPublisher Publisher { get; init; }
        public DynamicYarpConfigService Config => App.Services.GetRequiredService<DynamicYarpConfigService>();
        public static async Task<Fixture> Create(string? directory = null, ILoggerProvider? logs = null)
        {
            var path = directory ?? Path.Combine(Path.GetTempPath(), "aneiang-transaction-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(path);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = path, EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            if (logs is not null)
            {
                builder.Logging.AddProvider(logs).AddFilter((_, _) => true);
            }

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Gateway:Storage:Sqlite:ConnectionString"] = "Data Source=" + Path.Combine(path, "gateway.db") });
            builder.Services.AddWafIshStorage().AddWafIshRuntime();
            builder.Services.AddSingleton<IPluginActivationState, AllowAllPluginActivationState>();
            builder.Services.AddSingleton<SqliteConfigurationSnapshotRepository>();
            builder.Services.AddSingleton<BlockingStore>();
            builder.Services.AddSingleton<IConfigurationSnapshotRepository>(sp => sp.GetRequiredService<BlockingStore>());
            var publisher = new FailingPublisher();
            builder.Services.AddSingleton<IGatewaySnapshotPublisher>(publisher);
            var app = builder.Build();
            var fixture = new Fixture { App = app, Directory = path, OwnsDirectory = directory is null,
                Store = app.Services.GetRequiredService<BlockingStore>(), Publisher = publisher };
            await ((IHostedService)fixture.Config).StartAsync(default);
            return fixture;
        }
        public async Task Sql(string text)
        {
            await using var conn = App.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand(); cmd.CommandText = text; await cmd.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (OwnsDirectory)
            {
                System.IO.Directory.Delete(Directory, true);
            }
        }
    }
    internal sealed class BlockingStore(SqliteConfigurationSnapshotRepository inner) : IConfigurationSnapshotRepository
    {
        public Func<Task>? BeforeSave { get; set; }
        public Task<ConfigurationSnapshot> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public async Task SaveAsync(ConfigurationSnapshot snapshot, long expectedVersion, CancellationToken ct = default)
        {
            if (BeforeSave is { } before) { BeforeSave = null; await before(); }
            await inner.SaveAsync(snapshot, expectedVersion, ct);
        }
    }
    internal sealed class FailingPublisher : IGatewaySnapshotPublisher
    {
        private readonly GatewaySnapshotPublisher _inner = new();
        public bool Fail { get; set; }
        public Action? BeforePublish { get; set; }
        public GatewaySnapshot Current => _inner.Current;
        public GatewaySnapshot Publish(GatewaySnapshot snapshot)
        {
            BeforePublish?.Invoke();
            return Fail ? throw new InvalidOperationException("Injected activation failure") : _inner.Publish(snapshot);
        }
    }
}
