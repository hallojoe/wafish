using System.Diagnostics.Metrics;
using Wafish.Plugin.Metrics;
using Wafish.Plugins;

internal static class MetricContractChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Metric contracts: units, reset behavior, descriptors, and collector counts agree", () =>
        {
            using var store = new PluginMetricStore();
            using var listener = new MeterListener();
            var counts = new Dictionary<string, long>();
            var durations = new Dictionary<string, double>();
            listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "Wafish.Plugin.Metrics") owner.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            {
                Require(tags.Length == 1 && tags[0].Key.EndsWith(".id"), "Unexpected metric dimension.");
                counts[instrument.Name] = value;
                Require(instrument.Unit == (instrument.Name.EndsWith("bytes") ? "By" : "{request}"), "Counter unit mismatch.");
            });
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            {
                Require(instrument.Unit == "s", "Duration export is not seconds."); durations[instrument.Name] = value;
            });
            listener.Start();
            store.Record("r", "c", null, 200, 1_500_000, 12, 34);
            store.Record("r", "c", null, 503, -1, -1, -1);
            listener.RecordObservableInstruments();
            Require(counts["wafish.route.requests"] == 2 && counts["wafish.cluster.requests"] == 2, "Collector duplicated route and cluster attempts.");
            Require(counts["wafish.route.errors"] == 1 && counts["wafish.route.request_bytes"] == 12 && counts["wafish.route.response_bytes"] == 34,
                "Exported counters changed units or outcomes.");
            Require(durations["wafish.route.duration"] == 1.5 && store.GetRoutes()["r"].DurationMicroseconds == 1_500_000, "Duration conversion failed.");
            using var restarted = new PluginMetricStore();
            Require(restarted.GetRoutes().Count == 0, "Store recreation retained counters.");
            var snapshot = new PluginRuntimeResourceSnapshot("r", "test", true, PluginResourceHealthStatus.Healthy, null, null, null,
                new Dictionary<string, long> { ["memoryBytes"] = 100, ["writtenEntries"] = 2, ["durationMicroseconds"] = 10, ["vendor.custom"] = 7 });
            Require(snapshot.Descriptors["memoryBytes"].Estimated && snapshot.Descriptors["writtenEntries"].Kind == PluginStatisticKind.Counter &&
                snapshot.Descriptors["durationMicroseconds"].Unit == "us" && snapshot.Statistics["vendor.custom"] == 7 && !snapshot.Descriptors.ContainsKey("vendor.custom"), "Descriptor compatibility failed.");
            return Task.CompletedTask;
        });
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
