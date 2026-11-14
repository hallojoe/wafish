using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Resilience;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.Infrastructure.State;
using Wafish.Models;
using Wafish.Runtime;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

internal static class NeedSixMaintenanceChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Maintenance: bounded windows expire monotonically and preserve separate circuit failure state", () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime();
            var store = new InMemoryDestinationMaintenanceStore(clock);
            var circuits = new InMemoryCircuitStateStore(clock);
            var policy = new CircuitBreakerConfig { MinimumThroughput = 1, RecoveryTimeoutSeconds = 5 };
            circuits.RecordOutcome("cluster", "one", true, config: policy);
            store.Set("cluster", "one", "operator", "repair", TimeSpan.FromSeconds(10), true);
            Check(!store.AllowsTraffic("cluster", "one") && store.AllowsTraffic("cluster", "two"), "Window affected unrelated traffic.");
            Check(!circuits.IsRecoveryProbeEligible("cluster", "one"), "Early recovery was eligible.");
            clock.ShiftUtc(TimeSpan.FromDays(5));
            Check(!store.AllowsTraffic("cluster", "one"), "Wall-clock jump expired maintenance.");
            clock.Advance(TimeSpan.FromSeconds(5));
            Check(circuits.IsRecoveryProbeEligible("cluster", "one"), "Eligible recovery was hidden.");
            using var probe = circuits.TryAcquireAttempt("cluster", "one", policy);
            Check(probe is { IsRecoveryProbe: true } && store.AllowsTraffic("cluster", "one", probe.IsRecoveryProbe), "Recovery probe was not admitted.");
            Check(circuits.TryAcquireAttempt("cluster", "one", policy) is null, "Maintenance bypassed probe limits.");
            probe!.Complete(false);
            Check(!circuits.IsRecoveryProbeEligible("cluster", "one") && !store.AllowsTraffic("cluster", "one"), "Recovery admitted ordinary maintenance traffic.");
            clock.Advance(TimeSpan.FromSeconds(5));
            Check(store.AllowsTraffic("cluster", "one") && store.GetAll().Count == 0, "Expiry failed to restore eligibility.");
            store.Set("cluster", "one", "operator", "repair", TimeSpan.FromSeconds(10), false);
            Check(!store.AllowsTraffic("cluster", "one", true) && store.Remove("cluster", "one") && !store.Remove("cluster", "one"), "Cancellation or probe restriction failed.");
            foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromDays(2) })
                try { store.Set("c", "d", "actor", "reason", duration, false); throw new Exception("Invalid duration accepted."); } catch (ArgumentException) { }
            try { store.Set("c", "d", "actor", "\n", TimeSpan.FromSeconds(1), false); throw new Exception("Invalid reason accepted."); } catch (ArgumentException) { }
            for (var i = 0; i < 10000; i++) store.Set("c", i.ToString(), "actor", "reason", TimeSpan.FromSeconds(1), false);
            try { store.Set("c", "overflow", "actor", "reason", TimeSpan.FromSeconds(1), false); throw new Exception("Capacity exceeded."); } catch (InvalidOperationException) { }
            clock.Advance(TimeSpan.FromSeconds(1));
            Check(store.GetAll().Count == 0, "Expired capacity did not recover.");
            return Task.CompletedTask;
        });
        await run("Maintenance traffic: selected destinations and retries are filtered while in-flight responses finish", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backendBuilder = WebApplication.CreateBuilder(); backendBuilder.Logging.ClearProviders(); backendBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var backend = backendBuilder.Build();
            backend.MapGet("/{**path}", async (HttpContext context) =>
            {
                if (context.Request.Path.Value!.EndsWith("hold")) { entered.TrySetResult(); await release.Task; }
                await context.Response.WriteAsync(context.Request.Path.Value!);
            });
            await backend.StartAsync();
            var clock = new ReviewReleaseChecks.ManualTime();
            var maintenance = new InMemoryDestinationMaintenanceStore(clock);
            var circuits = new InMemoryCircuitStateStore(clock);
            var plans = new GatewayPluginExecutionPlanProvider(new GatewaySnapshotPublisher(), NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            var coordinator = new DestinationCandidateCoordinator(circuits, plans, maintenance);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IDestinationMaintenanceStore>(maintenance).AddSingleton<ICircuitStateStore>(circuits).AddSingleton(plans);
            builder.Services.AddReverseProxy().LoadFromMemory([new RouteConfig { RouteId = "r", ClusterId = "cluster", Match = new() { Path = "/{**path}" } }],
                [new ClusterConfig { ClusterId = "cluster", Destinations = new Dictionary<string, DestinationConfig>
                { ["one"] = new() { Address = backend.Urls.Single() + "/one/" }, ["two"] = new() { Address = backend.Urls.Single() + "/two/" } } }]);
            await using var gateway = builder.Build();
            gateway.MapWafishProxy(proxy =>
            {
                proxy.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/retry")
                    {
                        maintenance.Set("cluster", "one", "operator", "repair", TimeSpan.FromSeconds(10), false);
                        await coordinator.ApplyAsync(context, false);
                    }
                    await next();
                });
                proxy.UseLoadBalancing();
            }, useBuiltInPipeline: false);
            await gateway.StartAsync();
            using var client = new HttpClient { BaseAddress = new(gateway.Urls.Single()) };
            Check(await client.GetStringAsync("/retry") == "/two/retry", "Retry selection ignored a new maintenance window.");
            var pending = client.GetStringAsync("/hold"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            maintenance.Set("cluster", "two", "operator", "repair", TimeSpan.FromSeconds(10), false);
            release.SetResult(); Check(await pending == "/two/hold", "Maintenance interrupted an in-flight response.");
            Check((await client.GetAsync("/new")).StatusCode == HttpStatusCode.ServiceUnavailable, "All maintenance destinations received new traffic.");
            clock.Advance(TimeSpan.FromSeconds(10));
            Check((await client.GetAsync("/new")).IsSuccessStatusCode, "Expired windows did not restore forwarding.");
            Check(circuits.Count == 0, "Maintenance changed circuit failure statistics.");
        });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
