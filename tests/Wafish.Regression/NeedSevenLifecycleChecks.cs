using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Plugins;
using Wafish.Dashboard.Infrastructure.Diagnostics;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Services;
using Wafish.Storage;
using Yarp.ReverseProxy.Configuration;

internal static class NeedSevenLifecycleChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await NeedSevenClientChecks.RunAsync(run);
        await run("Registration leases: ownership, renewal, drain, expiry, restart, and replacement remain instance scoped", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            await using var host = await ConfigurationTransactionChecks.Fixture.Create(configure: s => s.AddSingleton<TimeProvider>(clock));
            var config = host.Config;
            var first = new string('A', 64); var second = new string('B', 64);
            var grant = await config.RegisterLeasedInstanceAsync("service", "one", Cluster(), [Route()], first, 15);
            Check(grant.Outcome.Success && grant.Lease is { RenewalSeconds: 5, GraceSeconds: 30 }, grant.Outcome.Message);
            Check((await config.RegisterLeasedInstanceAsync("service", "two", Cluster(), [Route()], second, 60)).Outcome.Success, "Peer registration failed.");
            var uid = config.GetDynamicConfig()!.Routes.Single().RouteUid;
            Check(!(await config.TryAddRoute(new Wafish.Models.RegisterRouteRequest { RouteName = "service", ClusterName = "shared", MatchPath = "/service" })).Success, "HTTP registration bypassed lease ownership.");
            Check(!(await config.TryRemoveRoute("service")).Success, "HTTP removal bypassed lease ownership.");
            Check(!(await config.ImportBatchAsync([Cluster()], [Route()], "grpc")).Success, "Legacy gRPC import bypassed ownership.");
            Check(!(await config.RegisterInstanceAsync("service", "one", Cluster(), [Route()])).Success, "Legacy registration stole a lease.");
            Check(!(await config.RegisterLeasedInstanceAsync("service", "one", Cluster(), [Route()], second, 15)).Outcome.Success, "Replacement stole a lease.");
            Check(!(await config.RenewInstanceLeaseAsync("service", "one", second)).Outcome.Success, "Peer renewed another lease.");
            Check(!(await config.UnregisterInstanceAsync("service", "one")).Success, "Legacy unregister removed a leased instance.");
            Check(!(await config.UpdateInstanceDestinationsAsync("service", "one", new Dictionary<string, string> { ["one"] = "http://localhost:9001/" }, config.GetDynamicConfig()!.Version)).Success, "Legacy destination update stole a lease.");
            clock.Advance(TimeSpan.FromSeconds(10));
            Check((await config.RenewInstanceLeaseAsync("service", "one", first)).Outcome.Success, "Renewal failed.");
            Check((await config.UpdateLeasedInstanceDestinationsAsync("service", "one", new Dictionary<string, string> { ["one"] = "http://localhost:9001/" }, config.GetDynamicConfig()!.Version, first)).Success, "Owned update failed.");
            Check((await config.RenewInstanceLeaseAsync("service", "one", first, drain: true)).Lease!.Draining, "Drain failed.");
            Check(host.Publisher.Current.Clusters.Single().Destinations!.Count == 1, "Draining instance still receives new traffic.");
            Check(!(await config.RegisterLeasedInstanceAsync("service", "one", Cluster(), [Route()], first, 15)).Outcome.Success, "Registration revived a draining lease.");
            Check(!(await config.UpdateLeasedInstanceDestinationsAsync("service", "one", new Dictionary<string, string> { ["one"] = "http://localhost:9001/" }, config.GetDynamicConfig()!.Version, first)).Success, "Update revived a draining lease.");
            Check(config.GetDynamicConfig()!.Routes.Single().RouteUid == uid, "Lease operations changed route identity.");
            await using var restart = await ConfigurationTransactionChecks.Fixture.Create(host.Directory, configure: s => s.AddSingleton<TimeProvider>(clock));
            Check(restart.Config.IsLeasedInstance("service", "one") && restart.Publisher.Current.Clusters.Single().Destinations!.Count == 1, "Restart lost lease or drain state.");
            config = restart.Config;
            clock.Advance(TimeSpan.FromSeconds(45));
            Check((await config.ExpireInstanceLeasesAsync()).Success, "Expiry failed.");
            Check(!config.IsLeasedInstance("service", "one") && config.IsLeasedInstance("service", "two"), "Expiry removed a healthy peer.");
            var third = new string('C', 64);
            Check((await config.RegisterLeasedInstanceAsync("service", "one", Cluster(), [Route()], third, 15)).Outcome.Success, "Expired instance replacement failed.");
            Check(!(await config.RenewInstanceLeaseAsync("service", "one", first)).Outcome.Success, "Old credential renewed a new lease.");
            Check(!(await config.UnregisterLeasedInstanceAsync("service", "one", first)).Success, "Old credential removed a new lease.");
            Check((await config.UnregisterLeasedInstanceAsync("service", "one", third)).Success, "Owner could not unregister.");
            Check((await config.RegisterInstanceAsync("service", "legacy", Cluster(), [Route()])).Success, "Legacy peer compatibility failed.");
            clock.Advance(TimeSpan.FromHours(1)); await config.ExpireInstanceLeasesAsync();
            Check(config.IsInstanceRegistered("service", "legacy") && !config.IsLeasedInstance("service", "two"), "Lease cleanup removed a legacy peer.");
            Check(!(await config.RegisterLeasedInstanceAsync("service", "bad", Cluster(), [Route()], "invalid", 1)).Outcome.Success, "Invalid lease accepted.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await config.ExpireInstanceLeasesAsync(cancelled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        });
        await run("WAF exclusions: registered mutation services audit trusted in-process changes", async () =>
        {
            await using var host = await ConfigurationTransactionChecks.Fixture.Create();
            Check((await host.Config.ImportBatchAsync([Cluster()], [Route()])).Success, "Configuration failed.");
            var service = host.App.Services.GetRequiredService<IPluginBindingMutationService>();
            var binding = new Wafish.Storage.Entities.PluginBindingEntity { Id = "excluded", PluginId = "waf", PluginVersion = "1.0",
                Scope = Wafish.Storage.Entities.PluginBindingScope.Route, ScopeId = "service", SchemaVersion = 1, ConfigVersion = 1, Enabled = true,
                ConfigJson = "{\"exclusions\":[{\"ruleId\":\"xss\",\"target\":\"Query\",\"field\":\"example\"}]}" };
            await service.UpsertAsync(binding);
            await service.UpsertAsync(binding);
            Check(await service.DeleteAsync(binding.Id), "Delete failed.");
            var audits = host.App.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent().Where(a => a.Action == "WafExclusions").ToArray();
            Check(audits.Length == 2 && audits.All(a => a.Operator == "plugin-binding-service"), "Service exclusion audit is incomplete.");
        });
        await run("Plugin drain diagnostics: held generations stay visible until their final request exits", async () =>
        {
            await using var host = await ConfigurationTransactionChecks.Fixture.Create();
            var manager = host.App.Services.GetRequiredService<PluginRuntimeDomainManager>();
            Check((await manager.TransitionAsync([])).Succeeded, "Initial transition failed.");
            var generation = manager.GetDrainSnapshot().Single(d => !d.Retiring).Generation;
            Check(manager.Current.TryAcquire(out var lease), "Request lease failed.");
            try
            {
                Check((await manager.TransitionAsync([])).Succeeded, "Replacement failed.");
                var held = manager.GetDrainSnapshot().Single(d => d.Generation == generation);
                Check(held.ActiveRequests == 1 && held.State == "Draining" && held.Retiring, "Held domain disappeared.");
            }
            finally { lease.Dispose(); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (manager.GetDrainSnapshot().Any(d => d.Generation == generation)) await Task.Delay(5, timeout.Token);
            await manager.DisposeAsync();
            Check(manager.GetDrainSnapshot().Count == 0, "Completed domains remained visible.");
        });
        await run("Plugin drain diagnostics: disposal failures survive cleanup without exposing exception details", async () =>
        {
            await using var host = await ConfigurationTransactionChecks.Fixture.Create();
            var manager = new PluginRuntimeDomainManager(host.App.Services, host.App.Services.GetRequiredService<ExternalGatewayPluginHost>(), NullLogger<PluginRuntimeDomainManager>.Instance);
            var domain = new PluginRuntimeDomain(new ServiceCollection().BuildServiceProvider(), new Dictionary<string, IGatewayPlugin>(),
                _ => Task.CompletedTask, _ => Task.CompletedTask, [new FailedDisposal()]);
            await manager.CommitAsync(domain);
            await manager.CommitAsync(new PluginRuntimeDomain(new ServiceCollection().BuildServiceProvider(), new Dictionary<string, IGatewayPlugin>(),
                _ => Task.CompletedTask, _ => Task.CompletedTask, []));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!manager.GetDrainSnapshot().Any(d => d.State == "Failed")) await Task.Delay(5, timeout.Token);
            try { await manager.DisposeAsync(); throw new Exception("Disposal failure disappeared."); } catch (AggregateException) { }
            Check(manager.GetDrainSnapshot().Single().State == "Failed", "Failed generation was not retained.");
            Check(!JsonSerializer.Serialize(manager.GetDrainSnapshot()).Contains("secret"), "Disposal diagnostics leaked exception details.");
        });
        await run("Registration lease reaper: scheduled cleanup expires only leased instances and stops its timer", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            await using var host = await ConfigurationTransactionChecks.Fixture.Create(configure: s => s.AddSingleton<TimeProvider>(clock));
            Check((await host.Config.RegisterLeasedInstanceAsync("service", "one", Cluster(), [Route()], new string('A', 64), 15)).Outcome.Success, "Registration failed.");
            var reaper = host.App.Services.GetServices<IHostedService>().Single(s => s.GetType().Name == "RegistrationLeaseReaper");
            await reaper.StartAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (clock.TimerCount == 0) await Task.Delay(5, timeout.Token);
            clock.Advance(TimeSpan.FromSeconds(45));
            while (host.Config.IsInstanceRegistered("service", "one")) await Task.Delay(5, timeout.Token);
            await reaper.StopAsync(default);
            Check(clock.TimerCount == 0, "Reaper retained its timer.");
        });
        await run("Diagnostic bundles: safe projections, deadlines, partial failure, and admission remain bounded", async () =>
        {
            var blocked = new BlockingCapabilities();
            await using var host = await ConfigurationTransactionChecks.Fixture.Create(configure: s => s.AddSingleton<IStorageCapabilitiesProvider>(blocked));
            var service = new DiagnosticBundleService(host.App.Services);
            var bytes = await service.CollectAsync().WaitAsync(TimeSpan.FromSeconds(4));
            Check(bytes.Length < DiagnosticBundleService.MaximumBytes, "Bundle exceeded byte limit.");
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            Check(json.Contains("storage:timeout") && json.Contains("revisions") && !json.Contains("secret"), "Partial bundle omitted failures or exposed secrets.");
            Check(System.Text.Encoding.UTF8.GetString(await service.CollectAsync()).Contains("collection-busy"), "A blocked provider allowed another probe.");
            blocked.Release.SetResult();
            byte[] next;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do { await Task.Delay(5, timeout.Token); next = await service.CollectAsync(); }
            while (System.Text.Encoding.UTF8.GetString(next).Contains("collection-busy"));
            json = System.Text.Encoding.UTF8.GetString(next);
            Check(json.Contains("custom") && !json.Contains("secret") && JsonDocument.Parse(json).RootElement.GetProperty("failures").GetArrayLength() == 0, "Bundle did not recover safely.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await service.CollectAsync(cancelled.Token); throw new Exception("Cancelled bundle succeeded."); } catch (OperationCanceledException) { }
        });
    }
    private sealed class FailedDisposal : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("secret"));
    }
    private sealed class BlockingCapabilities : IStorageCapabilitiesProvider
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<StorageCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        { await Release.Task; return new("secret-provider", StorageFeature.AtomicConfiguration, true); }
    }
    private static ClusterConfig Cluster() => new() { ClusterId = "shared", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://localhost:9000/" } } };
    private static RouteConfig Route() => new() { RouteId = "service", ClusterId = "shared", Match = new() { Path = "/service/{**rest}" } };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
