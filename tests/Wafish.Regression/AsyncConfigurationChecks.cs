using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wafish.Models;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;
using Fixture = ConfigurationTransactionChecks.Fixture;

internal static class AsyncConfigurationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Configuration async: delayed validation yields and cancellation preserves both revisions", async () =>
        {
            var validator = new DelayedValidator();
            await using var fixture = await Fixture.Create(configure: services => services.AddSingleton<IConfigValidator>(validator));
            var original = await fixture.Store.LoadAsync();
            validator.Delay = true;
            using var cancellation = new CancellationTokenSource();
            var mutation = fixture.Config.ImportBatchAsync([Cluster("new")], [Route("new", "new")], "test", "test", null, cancellation.Token);
            Check(!mutation.IsCompleted && validator.Entered.Task.IsCompleted, "Validation blocked the caller or ran synchronously.");
            cancellation.Cancel();
            await Cancelled(mutation);
            var after = await fixture.Store.LoadAsync();
            Check(after.Version == original.Version && fixture.Config.GetDynamicConfig()!.Version == original.Version, "Cancelled validation changed revisions.");
            Check(after.Routes.Count == original.Routes.Count && fixture.Config.GetRoutes().Count == 0, "Cancelled candidate escaped.");
            validator.Delay = false; validator.Release.TrySetResult([]);
            Check((await fixture.Config.ImportBatchAsync([Cluster("next")], [Route("next", "next")])).Success, "Cancellation leaked the publication lock.");
        });

        await run("Configuration async: lock wait and startup loading observe caller cancellation", async () =>
        {
            await using var fixture = await Fixture.Create(start: false);
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Store.BeforeLoad = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
            var startup = ((IHostedService)fixture.Config).StartAsync(cancellation.Token);
            await entered.Task;
            cancellation.Cancel();
            await Cancelled(startup);
            Check(fixture.Publisher.Current.Version == 0 && (await fixture.Store.LoadAsync()).Version == 0, "Cancelled startup published or saved configuration.");
            await ((IHostedService)fixture.Config).StartAsync(default);
            var gate = GatewayPublicationCoordinator.For(fixture.Publisher).Gate;
            await gate.WaitAsync();
            try
            {
                using var blocked = new CancellationTokenSource();
                var mutation = fixture.Config.TryAddClusterConfig(Cluster("blocked"), "test", "test", blocked.Token);
                blocked.Cancel();
                await Cancelled(mutation);
            }
            finally { gate.Release(); }
            Check(!fixture.Config.GetClusters().Any(c => c.ClusterId == "blocked"), "Cancelled lock wait mutated configuration.");
        });

        await run("Configuration async: cancellation after storage starts still completes activation and audit", async () =>
        {
            await using var fixture = await Fixture.Create();
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Store.BeforeSave = async () => { entered.SetResult(); await release.Task; };
            var mutation = fixture.Config.TryAddClusterConfig(Cluster("committed"), "test", "test", cancellation.Token);
            await entered.Task;
            cancellation.Cancel();
            Check(!mutation.IsCompleted, "Cancellation abandoned an owned commit.");
            release.SetResult();
            var result = await mutation;
            Check(result.Success && result.PersistedVersion == result.ActiveVersion, "Committed revision was not activated.");
            Check((await fixture.Store.LoadAsync()).Clusters.Any(c => c.ClusterId == "committed") && fixture.Config.GetClusters().Any(c => c.ClusterId == "committed"), "Commit lost stored or active configuration.");
            Check(fixture.App.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent(100).Any(e => e.Success), "Committed mutation lost its audit outcome.");
        });

        await run("Configuration async: generated concurrent rename, delete, registration, and stale updates preserve identities", async () =>
        {
            await using var fixture = await Fixture.Create();
            for (var seed = 0; seed < 8; seed++)
            {
                var name = "seed-" + seed;
                Check((await fixture.Config.ImportBatchAsync([Cluster(name)], [Route(name, name)])).Success, "Sequence setup failed.");
                var uid = fixture.Config.GetDynamicConfig()!.Routes.Single(r => r.Config.RouteId == name).RouteUid;
                var request = new RegisterRouteRequest { RouteName = name + "-renamed", ClusterName = name, MatchPath = "/" + name, DestinationAddress = "http://localhost:12345/" };
                var operations = new List<Func<Task>>
                {
                    async () => { await fixture.Config.TryRenameRoute(name, name + "-renamed", request); },
                    async () => { await fixture.Config.TryRemoveRoute(name, null, false); },
                    async () => { Check((await fixture.Config.RegisterInstanceAsync("service-" + name, "one", Cluster("service-" + name), [Route("service-" + name, "service-" + name)])).Success, "Instance A failed."); },
                    async () => { Check((await fixture.Config.RegisterInstanceAsync("service-" + name, "two", Cluster("service-" + name), [Route("service-" + name, "service-" + name)])).Success, "Instance B failed."); }
                };
                var random = new Random(seed);
                await Task.WhenAll(operations.OrderBy(_ => random.Next()).Select(action => Task.Run(action)));
                var renamed = fixture.Config.GetDynamicConfig()!.Routes.FirstOrDefault(r => r.Config.RouteId == name + "-renamed");
                Check(renamed == null || renamed.RouteUid == uid, "Rename changed stable route identity.");
                var staleVersion = fixture.Config.GetDynamicConfig()!.Version;
                Check((await fixture.Config.TryAddClusterConfig(Cluster("extra-" + name))).Success, "Revision advance failed.");
                var rejected = await fixture.Config.UpdateInstanceDestinationsAsync("service-" + name, "one", new Dictionary<string, string> { ["d"] = "http://localhost:12346/" }, staleVersion);
                Check(rejected.Status == ConfigurationUpdateStatus.VersionConflict, "Stale registration update was accepted.");
                var stored = await fixture.Store.LoadAsync();
                var active = fixture.Config.GetDynamicConfig()!;
                Check(stored.Version == active.Version, "Stored and active revisions diverged.");
                Check(stored.Routes.Select(r => r.RouteId).Order().SequenceEqual(active.Routes.Select(r => r.Config.RouteId).Order()), "Concurrent operations lost routes.");
                Check(active.Routes.Select(r => r.RouteUid).Distinct().Count() == active.Routes.Count, "Route identities collided.");
                Check(fixture.Config.GetClusters().Single(c => c.ClusterId == "service-" + name).Destinations!.Count == 2, "Concurrent registration lost a peer.");
            }
        });
    }
    private static ClusterConfig Cluster(string id) => new() { ClusterId = id, Destinations = new Dictionary<string, DestinationConfig> { ["d"] = new() { Address = "http://localhost:12345/" } } };
    private static RouteConfig Route(string id, string cluster) => new() { RouteId = id, ClusterId = cluster, Match = new() { Path = "/" + id } };
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected caller cancellation."); }
        catch (OperationCanceledException) { }
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
    private sealed class DelayedValidator : IConfigValidator
    {
        public bool Delay;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IList<Exception>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IList<Exception>> ValidateRouteAsync(RouteConfig route)
        {
            if (!Delay) { return ValueTask.FromResult<IList<Exception>>([]); }
            Entered.TrySetResult(); return new(Release.Task);
        }
        public ValueTask<IList<Exception>> ValidateClusterAsync(ClusterConfig cluster) => ValueTask.FromResult<IList<Exception>>([]);
    }
}
