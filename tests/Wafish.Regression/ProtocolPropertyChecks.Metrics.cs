using Wafish.Plugin.Metrics;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

internal static partial class ProtocolPropertyChecks
{
    private static async Task MetricsAsync()
    {
        foreach (var seed in DeterministicFaultSchedule.Seeds)
        {
            var publisher = new GatewaySnapshotPublisher();
            var snapshot = GatewaySnapshot.Empty with { Version = 1, Routes = [new RouteConfig { RouteId = "route", Match = new() { Path = "/route" } }],
                Clusters = [new ClusterConfig { ClusterId = "cluster", Destinations = new Dictionary<string, DestinationConfig> { ["destination"] = new() { Address = "http://127.0.0.1:9/" } } }] };
            publisher.Publish(snapshot);
            var store = new PluginMetricStore(publisher);
            const int workers = 8, perWorker = 256;
            var arrived = 0; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expectedErrors = 0; var expectedExceptions = 0; var expectedCancelled = 0;
            var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
            {
                var random = new Random(seed + worker);
                for (var index = 0; index < perWorker; index++)
                {
                    var outcome = (PluginMetricOutcome)random.Next(3);
                    var status = random.Next(2) == 0 ? 200 : 503;
                    store.Record("route", "cluster", "destination", status, 7, 3, 5, outcome);
                    if (outcome == PluginMetricOutcome.Cancelled) Interlocked.Increment(ref expectedCancelled);
                    else if (outcome == PluginMetricOutcome.Faulted || status >= 500) Interlocked.Increment(ref expectedErrors);
                    if (outcome == PluginMetricOutcome.Faulted) Interlocked.Increment(ref expectedExceptions);
                    if (index == 0) { if (Interlocked.Increment(ref arrived) == workers) ready.TrySetResult(); await release.Task; }
                    if (index % 8 == 0) await Task.Yield();
                }
            })).ToArray();
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(store.GetRoutes()["route"].Requests == workers, "Writers crossed the controlled snapshot barrier.");
                release.TrySetResult();
                var completion = Task.WhenAll(tasks); var reads = 0;
                do
                {
                    foreach (var metrics in new[] { store.GetRoutes(), store.GetClusters(), store.GetDestinations() })
                    foreach (var value in metrics.Values)
                    {
                        Check(value.Requests >= 0 && value.Errors <= value.Requests && value.Exceptions <= value.Errors && value.Errors + value.Cancellations <= value.Requests, "A concurrent metric snapshot mixed outcome counters.");
                        Check(value.RequestBytes == value.Requests * 3 && value.ResponseBytes == value.Requests * 5 && value.DurationMicroseconds == value.Requests * 7, "A concurrent metric snapshot mixed request and value totals.");
                    }
                    reads++; await Task.Yield();
                } while (!completion.IsCompleted);
                await completion.WaitAsync(TimeSpan.FromSeconds(10));
                foreach (var value in new[] { store.GetRoutes()["route"], store.GetClusters()["cluster"], store.GetDestinations()["cluster/destination"] })
                    Check(value.Requests == workers * perWorker && value.Errors == expectedErrors && value.Exceptions == expectedExceptions && value.Cancellations == expectedCancelled, "Concurrent updates lost a metric outcome.");
                publisher.Publish(GatewaySnapshot.Empty with { Version = 2 });
                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() => store.Record("route", "cluster", "destination", 200, 7, 3, 5))));
                Check(store.GetRoutes().Count + store.GetClusters().Count + store.GetDestinations().Count == 0, "Late concurrent completions revived retired identities.");
                publisher.Publish(snapshot with { Version = 3 });
                store.Record("route", "cluster", "destination", 200, 7, 3, 5);
                Check(store.GetRoutes()["route"].Requests == 1, "A reintroduced identity retained retired counters.");
                Console.WriteLine($"Metric properties: seed {seed}, {workers * perWorker} updates, {reads} coherent concurrent snapshot rounds.");
            }
            finally { release.TrySetResult(); await Task.WhenAll(tasks); }
        }
    }
}
