using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Plugin.Compression;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class CompressionSavingsChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Compression metrics: actual bytes include framing, bypasses, aborted writes, and bounded route labels", async () =>
        {
            var measurements = new ConcurrentBag<Measurement>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "Wafish.Plugin.Compression") owner.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var values = tags.ToArray().ToDictionary(p => p.Key, p => p.Value?.ToString());
                Check(values.Count == 3, "Compression dimensions included unbounded request data.");
                measurements.Add(new(instrument.Name, value, values["wafish.route.id"]!, values["compression.encoding"]!, values["compression.outcome"]!));
            });
            listener.Start();
            foreach (var encoding in new[] { "gzip", "br", "identity" })
            {
                var route = "savings-" + encoding;
                var payload = Encoding.UTF8.GetBytes(new string('a', 4096));
                var output = await Send(route, encoding, payload);
                Check(Sum("input_bytes", route) == payload.Length && Sum("output_bytes", route) == output.Length && Sum("responses", route) == 1, "Compression byte counts omitted framing or duplicated a response.");
                Check(measurements.Where(m => m.Route == route).All(m => m.Encoding == encoding && m.Outcome == "completed"), "Encoding or outcome attribution changed.");
                using var input = new MemoryStream(output);
                using Stream decoded = encoding switch { "br" => new BrotliStream(input, CompressionMode.Decompress), "gzip" => new GZipStream(input, CompressionMode.Decompress), _ => input };
                using var body = new MemoryStream(); await decoded.CopyToAsync(body);
                Check(body.ToArray().SequenceEqual(payload), "Metrics changed payload bytes.");
            }
            var tiny = await Send("savings-expansion", "gzip", [1, 2, 3]);
            Check(Sum("input_bytes", "savings-expansion") == 3 && Sum("output_bytes", "savings-expansion") == tiny.Length && tiny.Length > 3, "Compression expansion was hidden.");
            var unknown = await Send("savings-prefix", "gzip", new byte[32], knownLength: false, minimum: 24);
            Check(Sum("input_bytes", "savings-prefix") == 32 && Sum("output_bytes", "savings-prefix") == unknown.Length, "Prefix buffering double-counted input.");
            await Send("savings-small", "gzip", new byte[8], knownLength: false, minimum: 64);
            Check(Sum("input_bytes", "savings-small") == 8 && Sum("output_bytes", "savings-small") == 8 && measurements.Where(m => m.Route == "savings-small").All(m => m.Encoding == "identity"), "Small response bypass lost bytes.");
            await Send("savings-sync", "gzip", new byte[64], synchronous: true);
            Check(Sum("input_bytes", "savings-sync") == 64, "Synchronous writes lost bytes.");
            var aborted = await Send("savings-aborted", "gzip", new byte[64], abort: true);
            Check(Sum("input_bytes", "savings-aborted") == 32 && Sum("output_bytes", "savings-aborted") == aborted.Length &&
                measurements.Where(m => m.Route == "savings-aborted").All(m => m.Outcome == "aborted"), "Incomplete responses became completed savings.");
            var before = measurements.Count;
            await Send("savings-disabled", "gzip", new byte[4], enabled: false);
            Check(measurements.Count == before, "Disabled compression emitted measurements.");
            for (var i = 0; i < 160; i++) await Send("savings-churn-" + i, "identity", []);
            var labels = measurements.Select(m => m.Route).ToHashSet();
            Check(labels.Count <= 129 && labels.Contains("__other__"), "Route churn grew metric labels without a bound.");
            long Sum(string metric, string route) => measurements.Where(m => m.Name == "wafish.compression." + metric && m.Route == route).Sum(m => m.Value);
        });
    }
    private static async Task<byte[]> Send(string id, string accept, byte[] payload, bool knownLength = true, int minimum = 0, bool abort = false, bool enabled = true, bool synchronous = false)
    {
        var route = new RouteConfig { RouteId = id, Match = new() { Path = "/" + id } };
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with
        {
            Version = 1, Routes = [route],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(id,
                [new("metrics-compression", "compression", PluginBindingScope.Route, id, JsonSerializer.Serialize(new { enabled, minResponseSize = minimum }), 1, 1, 0)])
        });
        using var output = new MemoryStream();
        var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Headers.AcceptEncoding = accept;
        context.Response.Body = output; context.Response.ContentType = "application/json";
        if (knownLength) context.Response.ContentLength = payload.Length;
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        var middleware = new CompressionMiddleware(async c =>
        {
            if (synchronous) { c.Response.Body.Write(payload, 0, payload.Length); c.Response.Body.Flush(); return; }
            await c.Response.Body.WriteAsync(payload.AsMemory(0, payload.Length / 2));
            if (abort) throw new IOException("Injected producer failure.");
            await c.Response.Body.WriteAsync(payload.AsMemory(payload.Length / 2));
        }, new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
        try { await middleware.InvokeAsync(context); Check(!abort, "Expected producer failure was swallowed."); }
        catch (IOException) when (abort) { }
        Check(ReferenceEquals(context.Response.Body, output) && output.CanWrite, "Measurement wrapper changed stream ownership.");
        return output.ToArray();
    }
    private sealed record Measurement(string Name, long Value, string Route, string Encoding, string Outcome);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
