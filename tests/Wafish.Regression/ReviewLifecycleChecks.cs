using Wafish.Dashboard.Modules.Dashboard.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Wafish.Runtime;
using Wafish.Infrastructure.State;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.GatewayRegistry;
using Wafish.Models;
using Wafish.Plugin.ServiceDiscovery;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Fixture = ConfigurationTransactionChecks.Fixture;

internal static class ReviewLifecycleChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Review: atomic binding batches reject stale revisions and roll back partial writes", async () =>
        {
            await using var host = await Fixture.Create();
            var repo = host.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var initial = await repo.GetBindingsSnapshotAsync();
            var revision = await repo.CommitBindingsAsync(initial.Revision, [Binding("first"), Binding("second")], []);
            Check((await repo.GetBindingsSnapshotAsync()).Bindings.Count == 2, "Batch was incomplete.");
            try { await repo.CommitBindingsAsync(initial.Revision, [], ["first"]); throw new Exception("Stale revision accepted."); }
            catch (BindingRevisionConflictException ex) { Check(ex.ActualRevision == revision, "Wrong conflict revision."); }
            var invalid = Binding("invalid"); invalid.SchemaVersion = 0;
            try { await repo.CommitBindingsAsync(revision, [Binding("third"), invalid], ["first"]); throw new Exception("Invalid batch accepted."); }
            catch (ArgumentOutOfRangeException) { }
            var after = await repo.GetBindingsSnapshotAsync();
            Check(after.Revision == revision && after.Bindings.Select(b => b.Id).SequenceEqual(["first", "second"]), "Failed batch changed storage.");
            await repo.UpsertBindingAsync(Binding("direct"));
            try { await repo.CommitBindingsAsync(revision, [], ["first"]); throw new Exception("Direct write did not invalidate revision."); }
            catch (BindingRevisionConflictException) { }
        });

        await run("Review: administration batches are atomic and publish through the shared gate", async () =>
        {
            await using var host = await Fixture.Create();
            Check((await host.Config.ImportBatchAsync([Cluster()], [Route(), Route() with { RouteId = "second", Match = new() { Path = "/second" } }])).Success, "Setup failed.");
            var controller = ActivatorUtilities.CreateInstance<PluginBindingsController>(host.App.Services);
            var repo = host.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var request = new BatchCreateBindingsRequest { PluginId = "waf", Scope = "Route", ScopeIds = ["service", "second"], ConfigJson = "{\"enabled\":true}" };
            await host.Sql("CREATE TRIGGER reject_second BEFORE INSERT ON plugin_bindings WHEN NEW.scope_id = 'second' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            var generation = host.Publisher.Current.Version;
            try { await controller.BatchCreateBindings(request, default); throw new Exception("Partial batch accepted."); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            Check((await repo.GetBindingsAsync()).Count == 0 && host.Publisher.Current.Version == generation, "Partial batch changed storage or publication.");
            await host.Sql("DROP TRIGGER reject_second;");
            Check(await controller.BatchCreateBindings(request, default) is Microsoft.AspNetCore.Mvc.OkObjectResult, "Batch creation failed.");
            var ids = (await repo.GetBindingsAsync()).Select(b => b.Id).ToList();
            Check(ids.Count == 2 && host.Publisher.Current.RoutePlugins.Count == 2, "Batch publication omitted a binding.");
            host.Publisher.Fail = true;
            try { await controller.BatchSetBindingsEnabled(new() { BindingIds = ids, Enabled = false }, default); throw new Exception("Failed activation accepted."); }
            catch (InvalidOperationException) { }
            Check((await repo.GetBindingsAsync()).All(b => b.Enabled), "Compensation lost enabled bindings.");
            host.Publisher.Fail = false;
            Check(await controller.BatchSetBindingsEnabled(new() { BindingIds = ids, Enabled = false }, default) is Microsoft.AspNetCore.Mvc.OkObjectResult, "Batch disable failed.");
            Check(host.Publisher.Current.RoutePlugins.Values.All(b => b.Length == 0), "Disabled bindings remained active.");
            Check(await controller.BatchDeleteBindings(new() { BindingIds = ids }, default) is Microsoft.AspNetCore.Mvc.OkObjectResult, "Batch deletion failed.");
            Check((await repo.GetBindingsAsync()).Count == 0, "Batch deletion retained bindings.");
        });

        await run("Review: route storage, binding changes, and discovery share publication ownership", async () =>
        {
            await using var host = await Fixture.Create();
            Check((await host.Config.ImportBatchAsync([Cluster()], [Route()])).Success, "Initial configuration failed.");
            var repo = host.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var discovery = Binding("discovery"); discovery.PluginId = "service-discovery";
            discovery.Scope = PluginBindingScope.Cluster; discovery.ScopeId = "shared";
            discovery.ClusterUid = host.Config.GetDynamicConfig()!.Clusters.Single().ClusterUid;
            discovery.ConfigJson = "{\"enabled\":true,\"mode\":\"Static\",\"staticEndpoints\":[\"http://localhost:9001/\"]}";
            await repo.UpsertBindingAsync(discovery);
            host.Config.RefreshConfig();
            var service = ActivatorUtilities.CreateInstance<ServiceDiscoveryRefreshService>(host.App.Services);
            var binding = GatewayPublicationCoordinator.DiscoveryBinding(host.Publisher.Current, "shared");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Store.BeforeSave = () => { entered.SetResult(); return release.Task; };
            var routeChange = host.Config.TryAddRouteConfig(Route() with { RouteId = "second", Match = new() { Path = "/second" } });
            await entered.Task;
            var refresh = PublishDiscovery(service, ["http://localhost:9001/"], binding);
            var candidate = Binding("waf"); candidate.PluginId = "waf";
            candidate.RouteUid = host.Config.GetDynamicConfig()!.Routes.Single().RouteUid;
            var mutations = host.App.Services.GetRequiredService<IPluginBindingMutationService>();
            var mutation = mutations.UpsertAsync(candidate);
            Check(!refresh.IsCompleted && !mutation.IsCompleted, "A writer bypassed publication ownership.");
            release.SetResult();
            Check((await routeChange).Success, "Route activation lost its prepared generation.");
            await Task.WhenAll(refresh, mutation).WaitAsync(TimeSpan.FromSeconds(20));
            Check(host.Publisher.Current.Routes.Length == 2 && host.Publisher.Current.RoutePlugins["service"].Any(b => b.PluginId == "waf"), "A successful writer was lost.");
            var originalId = host.Publisher.Current.Clusters.Single().Destinations!.Single().Key;
            host.Config.RefreshConfig();
            Check(host.Publisher.Current.Clusters.Single().Destinations!.ContainsKey(originalId), "Route refresh lost discovery state.");
            await PublishDiscovery(service, ["http://localhost:9000/", "http://localhost:9001/"], binding);
            Check(host.Publisher.Current.Clusters.Single().Destinations!.ContainsKey(originalId), "Insertion changed a surviving destination identity.");
            await mutations.DeleteAsync("discovery");
            var generation = host.Publisher.Current.Version;
            await PublishDiscovery(service, ["http://localhost:9999/"], binding);
            Check(host.Publisher.Current.Version == generation, "Stale discovery replaced a removed binding.");
            Check(await mutations.DeleteAsync("waf") && !await mutations.DeleteAsync("missing"), "Binding deletion failed.");
        });

        await run("Review: binding storage failure preserves active state; activation failure uses revision-safe compensation", async () =>
        {
            await using var host = await Fixture.Create();
            Check((await host.Config.ImportBatchAsync([Cluster()], [Route()])).Success, "Initial configuration failed.");
            var mutations = host.App.Services.GetRequiredService<IPluginBindingMutationService>();
            var binding = Binding("waf"); binding.PluginId = "waf";
            binding.RouteUid = host.Config.GetDynamicConfig()!.Routes.Single().RouteUid;
            await host.Sql("CREATE TRIGGER reject_binding BEFORE INSERT ON plugin_bindings BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            var generation = host.Publisher.Current.Version;
            try { await mutations.UpsertAsync(binding); throw new Exception("Storage failure accepted."); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            Check(host.Publisher.Current.Version == generation, "Storage failure changed active state.");
            await host.Sql("DROP TRIGGER reject_binding;");
            host.Publisher.Fail = true;
            try { await mutations.UpsertAsync(binding); throw new Exception("Activation failure accepted."); }
            catch (InvalidOperationException) { }
            var repo = host.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            Check(await repo.GetBindingAsync("waf") == null, "Failed activation retained the new binding.");
            host.Publisher.Fail = false;
            await mutations.UpsertAsync(binding);
            host.Publisher.Fail = true;
            try { await mutations.DeleteAsync("waf"); throw new Exception("Failed deletion accepted."); }
            catch (InvalidOperationException) { }
            Check(await repo.GetBindingAsync("waf") != null, "Failed deletion lost its binding.");
            host.Publisher.Fail = false;
            await mutations.ReconcileAsync();
            await host.Config.ReconcileAsync();
            Check(host.Publisher.Current.RoutePlugins["service"].Any(b => b.PluginId == "waf"), "Reconciliation lost the stored policy.");
            host.Publisher.Fail = true;
            host.Publisher.BeforePublish = () =>
            {
                var newer = Binding("waf"); newer.PluginId = "waf"; newer.ConfigVersion = 99;
                repo.UpsertBindingAsync(newer).GetAwaiter().GetResult();
            };
            try { await mutations.UpsertAsync(binding); throw new Exception("Activation conflict accepted."); }
            catch (BindingRevisionConflictException) { }
            Check((await repo.GetBindingAsync("waf"))!.ConfigVersion == 99, "Compensation overwrote a newer successful write.");
            host.Publisher.Fail = false; host.Publisher.BeforePublish = null;
            await mutations.ReconcileAsync();
            host.Publisher.Fail = true;
            var result = await host.Config.TryAddRouteConfig(Route() with { RouteId = "stored", Match = new() { Path = "/stored" } });
            Check(result.Status == ConfigurationUpdateStatus.ActivationFailed, "Activation failure was hidden.");
            host.Publisher.Fail = false;
            await host.Config.ReconcileAsync();
            Check(host.Config.GetRoutes().Count == 2 && (await host.Config.TrySetRouteEnabled("stored", false)).Success,
                "Stored revision could not recover without restarting.");
        });

        await run("Review: instance updates preserve peers, reject stale revisions, and reject legacy requests", async () =>
        {
            await using var host = await Fixture.Create();
            foreach (var id in new[] { "one", "two" })
            {
                Check((await host.Config.RegisterInstanceAsync("service", id, Cluster(), [Route()])).Success, "Registration failed.");
            }

            var grpc = new GatewayRegistryGrpcService(host.Config, NullLogger<GatewayRegistryGrpcService>.Instance);
            var request = new UpdateDestinationsRequest { ServiceId = "service", InstanceId = "one", ExpectedVersion = host.Config.GetDynamicConfig()!.Version };
            request.Destinations.Add(new Destination { DestinationId = "new", Address = "http://localhost:9002/" });
            var response = await grpc.UpdateDestinations(request, new SecurityChecks.CallContext(new()));
            Check(response.Success, response.Message);
            var destinations = host.Config.GetClusters().Single().Destinations!;
            Check(destinations.ContainsKey("two:destination") && destinations.ContainsKey("one:new") && destinations.Count == 2, "Peer destination changed.");
            Check((await grpc.UpdateDestinations(request, new SecurityChecks.CallContext(new()))).Status == "VersionConflict", "Stale request accepted.");
            request.ExpectedVersion = response.ActiveVersion; request.InstanceId = "absent";
            Check(!(await grpc.UpdateDestinations(request, new SecurityChecks.CallContext(new()))).Success, "Unknown instance accepted.");
            request.InstanceId = "";
            Check(!(await grpc.UpdateDestinations(request, new SecurityChecks.CallContext(new()))).Success, "Legacy request replaced the cluster.");
            request.InstanceId = "one"; request.Destinations.Clear();
            Check(!(await grpc.UpdateDestinations(request, new SecurityChecks.CallContext(new()))).Success, "Empty update accepted.");
            Check(!(await host.Config.UpdateInstanceDestinationsAsync("service", "one", new Dictionary<string, string> { ["bad"] = "file:///tmp" }, response.ActiveVersion)).Success, "Invalid destination accepted.");
        });

        await run("Review: live forwarding reserves only the selected probe and records each retry once", async () =>
        {
            var backendBuilder = WebApplication.CreateBuilder();
            await using var backend = backendBuilder.Build();
            backend.Urls.Add("http://127.0.0.1:0");
            var failures = 0;
            backend.MapGet("/service", (HttpContext context) =>
            {
                context.Response.StatusCode = Interlocked.Decrement(ref failures) >= 0 ? 503 : 200;
                return Task.CompletedTask;
            });
            await backend.StartAsync();
            var address = backend.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var host = await Fixture.Create();
            Check((await host.Config.ImportBatchAsync([Cluster() with { Destinations = new Dictionary<string, DestinationConfig>
                { ["one"] = new() { Address = address }, ["two"] = new() { Address = address } } }], [Route()])).Success, "Setup failed.");
            var mutations = host.App.Services.GetRequiredService<IPluginBindingMutationService>();
            var circuit = Binding("circuit"); circuit.PluginId = "circuit-breaker"; circuit.Scope = PluginBindingScope.Cluster;
            circuit.ScopeId = "shared"; circuit.ClusterUid = host.Config.GetDynamicConfig()!.Clusters.Single().ClusterUid;
            circuit.ConfigJson = "{\"enabled\":true,\"minimumThroughput\":10,\"halfOpenMaxAttempts\":1,\"recoveryTimeoutSeconds\":1}";
            await mutations.UpsertAsync(circuit);
            var store = host.App.Services.GetRequiredService<ICircuitStateStore>();
            var openConfig = new CircuitBreakerConfig { Enabled = true, MinimumThroughput = 1, RecoveryTimeoutSeconds = 1 };
            foreach (var id in new[] { "one", "two" })
            {
                using var attempt = store.TryAcquireAttempt("shared", id, openConfig);
                attempt!.Complete(true);
            }
            foreach (var entry in store.GetAll())
            {
                entry.Value.OpenedAt = DateTime.Now.AddMinutes(-1);
            }

            host.App.Urls.Add("http://127.0.0.1:0");
            host.App.UseRouting(); host.App.MapWafishProxy();
            await host.App.StartAsync();
            try
            {
            var gateway = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(gateway) };
            using (var response = await client.GetAsync("/service"))
                {
                    Check(response.IsSuccessStatusCode, "Recovery probe failed: " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
                }

                Check(store.GetAll().Count(p => p.Value.Status == CircuitStatus.Closed && p.Value.DestinationKeySnapshot != "any") == 1 &&
                store.GetAll().All(p => p.Value.HalfOpenRequests == 0), "Unselected destination consumed a probe.");
            store.ResetAll();
            var retry = Binding("retry"); retry.PluginId = "request-retry";
            retry.RouteUid = host.Config.GetDynamicConfig()!.Routes.Single().RouteUid;
            retry.ConfigJson = "{\"enabled\":true,\"maxRetries\":2,\"useDifferentDestination\":false,\"backoffBaseMs\":1,\"backoffJitterMs\":0,\"retryOnStatusCodes\":[503]}";
            await mutations.UpsertAsync(retry);
            failures = 2;
            using (var response = await client.GetAsync("/service"))
                {
                    Check(response.IsSuccessStatusCode, "Retry did not recover.");
                }

                Check(store.GetAll().Sum(p => p.Value.Samples.Count) == 3 && store.GetAll().Sum(p => p.Value.SampleFailures) == 2,
                "Forwarding attempts recorded duplicate outcomes.");
            }
            finally { await host.App.StopAsync(); await backend.StopAsync(); }
        });

        await run("Review: circuit probe cancellation releases ownership and stale completions cannot close a newer circuit", () =>
        {
            var store = new InMemoryCircuitStateStore();
            var config = new CircuitBreakerConfig { Enabled = true, MinimumThroughput = 1, FailureRatio = .5, RecoveryTimeoutSeconds = 1, HalfOpenMaxAttempts = 2 };
            using (var attempt = store.TryAcquireAttempt("shared", "one", config))
            {
                attempt!.Complete(true);
            }

            var state = store.GetAll().Single().Value;
            Check(store.IsCircuitOpen("shared", "one") && store.TryAcquireAttempt("shared", "one", config) == null, "Open circuit admitted traffic.");
            state.OpenedAt = DateTime.Now.AddSeconds(-2);
            using (var canceled = store.TryAcquireAttempt("shared", "one", config))
            {
                Check(canceled != null, "Recovery probe unavailable.");
            }

            Check(state.HalfOpenRequests == 0, "Canceled probe leaked its reservation.");
            using var first = store.TryAcquireAttempt("shared", "one", config);
            using var second = store.TryAcquireAttempt("shared", "one", config);
            Check(first != null && second != null && store.TryAcquireAttempt("shared", "one", config) == null, "Probe limit failed.");
            first!.Complete(true); second!.Complete(false);
            Check(store.IsCircuitOpen("shared", "one"), "Late success closed a newer circuit.");
            state.OpenedAt = DateTime.Now.AddSeconds(-2);
            using var recovered = store.TryAcquireAttempt("shared", "one", config);
            recovered!.Complete(false);
            Check(state.Status == CircuitStatus.Closed, "Recovery did not close the circuit.");
            return Task.CompletedTask;
        });
    }

    private static PluginBindingEntity Binding(string id) => new() { Id = id, PluginId = id, PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "service", ConfigJson = "{\"enabled\":true}" };
    private static ClusterConfig Cluster() => new() { ClusterId = "shared", Destinations = new Dictionary<string, DestinationConfig> { ["destination"] = new() { Address = "http://localhost:9000/" } } };
    private static RouteConfig Route() => new() { RouteId = "service", ClusterId = "shared", Match = new() { Path = "/service" } };
    private static Task PublishDiscovery(ServiceDiscoveryRefreshService service, string[] endpoints, PluginBindingSnapshot? expected)
        => (Task)typeof(ServiceDiscoveryRefreshService).GetMethod("PublishDestinationsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, ["shared", endpoints, CancellationToken.None, expected])!;
    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
}
