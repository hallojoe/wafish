using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wafish.Models;
using Wafish.Plugin.Metrics;
using Wafish.Plugin.Waf;
using Wafish.Storage;

internal static class NeedSevenChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await NeedSevenLifecycleChecks.RunAsync(run);
        await run("Storage capabilities: startup rejects missing features and backup ownership releases files", async () =>
        {
            await using var fixture = await ConfigurationTransactionChecks.Fixture.Create();
            var capabilities = await fixture.App.Services.GetRequiredService<IStorageCapabilitiesProvider>().GetCapabilitiesAsync();
            Require(capabilities.Features.HasFlag(StorageFeature.ConsistentBackup | StorageFeature.AtomicConfiguration) && !capabilities.EncryptionEnabled, "SQLite capabilities are incorrect.");
            var provider = fixture.App.Services.GetRequiredService<IStorageBackupProvider>();
            await using var backup = await provider.CreateBackupAsync();
            var path = ((FileStream)backup.Content).Name;
            Require(backup.Content.Length > 0 && !backup.Encrypted && File.Exists(path), "Backup is empty.");
            await backup.DisposeAsync(); await backup.DisposeAsync();
            Require(!File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)), "Backup disposal leaked temporary files.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await provider.CreateBackupAsync(cancelled.Token); throw new Exception("Cancelled backup succeeded."); } catch (OperationCanceledException) { }
            foreach (var configured in new[] { false, true })
            {
                var services = new ServiceCollection().AddLogging();
                if (configured) services.AddSingleton(fixture.App.Services.GetRequiredService<IStorageCapabilitiesProvider>());
                services.AddWafishStorageRequirements(o => { o.Required = StorageFeature.ConsistentBackup; o.RequireEncryptionEnabled = true; });
                await using var root = services.BuildServiceProvider();
                try { await root.GetServices<IHostedService>().Single().StartAsync(default); throw new Exception("Unsupported requirements accepted."); }
                catch (InvalidOperationException) { }
            }
            var accepted = new ServiceCollection().AddSingleton(fixture.App.Services.GetRequiredService<IStorageCapabilitiesProvider>())
                .AddWafishStorageRequirements(o => o.Required = StorageFeature.AtomicConfiguration);
            await using var valid = accepted.BuildServiceProvider();
            await valid.GetServices<IHostedService>().Single().StartAsync(default);
            await valid.GetServices<IHostedService>().Single().StopAsync(default);
        });
        await run("Latency distributions: boundaries, overflow, concurrent samples, and telemetry retain exact counts", () =>
        {
            var observed = new List<double>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) => { if (instrument.Name == "wafish.route.latency") owner.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<double>((_, value, _, _) => { lock (observed) observed.Add(value); }); listener.Start();
            using var metrics = new PluginMetricStore();
            foreach (var duration in new long[] { 0, 1000, 1001, 5000, 10_000_001 }) metrics.Record("r", "c", null, 200, duration, 0, 0);
            var histogram = metrics.GetRoutes()["r"].Latency!;
            Require(histogram.Counts[0] == 2 && histogram.Counts[1] == 2 && histogram.Counts[^1] == 1 && histogram.UpperBoundsSeconds[^1] is null, "Latency bucket boundaries changed.");
            Parallel.For(0, 1000, _ => metrics.Record("r", "c", null, 200, 10_000, 0, 0));
            Require(metrics.GetRoutes()["r"].Latency!.Counts.Sum() == 1005 && metrics.GetClusters()["c"].Latency!.Counts.Sum() == 1005, "Concurrent samples were lost.");
            Require(observed.Count == 1005 && observed.Contains(0.001001), "Latency telemetry lost units or samples.");
            return Task.CompletedTask;
        });
        await run("Cache purge: route and tag selectors preserve unrelated entries and reject old fills", async () =>
        {
            var one = new ResponseStreamingChecks.Fixture(routeId: "one", tags: ["catalog"]);
            var two = new ResponseStreamingChecks.Fixture(routeId: "two", tags: ["catalog"], sharedStore: one.Store);
            var other = new ResponseStreamingChecks.Fixture(routeId: "other", tags: ["other"], sharedStore: one.Store);
            await one.Send(); await two.Send(); await other.Send();
            Require(one.Store.Purge("one") == 1, "Route purge removed the wrong entries.");
            await two.Send(); await other.Send(); Require(two.Calls == 1 && other.Calls == 1, "Route purge evicted peers.");
            await one.Send(); Require(one.Calls == 2, "Purged route remained cached.");
            Require(one.Store.Purge(tag: "catalog") == 2, "Tag purge missed a matching entry.");
            await other.Send(); Require(other.Calls == 1, "Tag purge evicted an unrelated entry.");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            one.Respond = async c => { entered.SetResult(); await release.Task; await c.Response.WriteAsync("stale"); };
            var pending = one.Send(); await entered.Task; one.Store.Purge("one"); release.SetResult(); await pending;
            one.Respond = null; await one.Send(); Require(one.Calls == 4, "An old fill restored a purged response.");
            try { one.Store.Purge(); throw new Exception("Unbounded purge accepted."); } catch (ArgumentException) { }
        });
        await run("WAF policies: detection-only reports matches while mandatory limits still block", async () =>
        {
            var options = new WafBindingOptions { EnableIpCheck = false, DetectionOnlyRules = ["sql-injection", "xss"], MaxRequestBodySize = 100 };
            var logs = new Capture();
            var payload = Encoding.UTF8.GetBytes("<script>SELECT secret</script>");
            var result = await WafBodyChecks.Invoke(payload, 100, binding: options, logger: logs);
            Require(result.Status == 200 && result.Forwarded!.SequenceEqual(payload), "Detection-only changed forwarding.");
            Require(logs.Messages.Count(m => m.Contains("detection-only")) == 2 && logs.Messages.All(m => !m.Contains("secret")), "Detection report omitted decisions or exposed content.");
            Require((await WafBodyChecks.Invoke(new byte[101], 100, binding: options)).Status == 413, "Detection-only bypassed size limits.");
            Require((await WafBodyChecks.Invoke([255], 100, binding: options)).Status == 400, "Detection-only bypassed decoding limits.");
        });
        await run("WAF exclusions: exact parsed fields leave sibling rules and fields protected", async () =>
        {
            var options = new WafBindingOptions { EnableIpCheck = false, Exclusions = [new("sql-injection", "Query", "example"), new("sql-injection", "Form", "example"), new("sql-injection", "Json", "/example")] };
            Require((await WafBodyChecks.Invoke([], 1000, query: "?example=SELECT", binding: options)).Status == 200, "Excluded query value blocked.");
            Require((await WafBodyChecks.Invoke([], 1000, query: "?example=SELECT&other=SELECT", binding: options)).Status == 403, "Unrelated query field bypassed inspection.");
            Require((await WafBodyChecks.Invoke(Encoding.UTF8.GetBytes("example=SELECT"), 1000, contentType: "application/x-www-form-urlencoded", binding: options)).Status == 200, "Excluded form value blocked.");
            foreach (var (body, code) in new[] { ("{\"example\":\"SELECT\",\"other\":\"safe\"}", 200), ("{\"example\":\"SELECT\",\"other\":\"SELECT\"}", 403), ("{\"example\":\"<script>\"}", 403), ("{\"example\":{\"nested\":\"SELECT\"}}", 400), ("{", 400) })
                Require((await WafBodyChecks.Invoke(Encoding.UTF8.GetBytes(body), 1000, contentType: "application/json", binding: options)).Status == code, "JSON exclusion changed protection: " + body);
        });
    }
    private sealed class Capture : ILogger<WafMiddleware>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
