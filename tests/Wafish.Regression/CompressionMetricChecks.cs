using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Models;
using Wafish.Plugin.Compression;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class CompressionMetricChecks
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(new string('a', 4096));
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Compression metrics: bytes include prefixes and trailers for both codecs and identity", async () =>
        {
            foreach (var encoding in new[] { "br", "gzip", "identity" })
            {
                using var store = new CompressionMetricStore(); using var sink = new MemoryStream();
                var context = Context(sink, encoding);
                await Invoke(store, context, async c =>
                {
                    await c.Response.Body.WriteAsync(Payload.AsMemory(0, 20));
                    c.Response.Body.Write(Payload, 20, 100);
                    await c.Response.Body.WriteAsync(Payload, 120, Payload.Length - 120);
                    await c.Response.Body.FlushAsync();
                }, minimum: 128);
                var (key, stats) = store.GetSnapshots().Single();
                Check(key.Encoding == encoding && stats.Responses == 1 && stats.InputBytes == Payload.Length && stats.OutputBytes == sink.Length, "Byte accounting lost prefix or footer bytes.");
                Check(ReferenceEquals(context.Response.Body, sink) && sink.CanWrite, "Middleware did not restore or retain the destination.");
                sink.Position = 0;
                using Stream decoder = encoding switch { "br" => new BrotliStream(sink, CompressionMode.Decompress, true), "gzip" => new GZipStream(sink, CompressionMode.Decompress, true), _ => sink };
                using var decoded = new MemoryStream(); await decoder.CopyToAsync(decoded);
                Check(decoded.ToArray().SequenceEqual(Payload), "Metrics changed the response representation.");
                Check(encoding == "identity" ? stats.CodecDurationTicks == 0 : stats.CodecDurationTicks > 0, "Codec duration was not measured separately from identity output.");
            }
            // Tiny compressed bodies can expand. Raw byte counts must retain that fact.
            using var expansion = new CompressionMetricStore(); using var expanded = new MemoryStream();
            await Invoke(expansion, Context(expanded), c => c.Response.Body.WriteAsync(new byte[] { 1 }).AsTask(), minimum: 0);
            var sample = expansion.GetSnapshots().Single().Value;
            Check(sample.OutputBytes > sample.InputBytes, "Expansion was hidden by a nonnegative savings counter.");
        });
        await run("Compression metrics: exclusions and early flush preserve identity bytes", async () =>
        {
            foreach (var mode in new[] { "head", "range", "request-no-transform", "response-no-transform", "status", "encoded", "binary", "small", "threshold", "flush", "empty", "malformed" })
            {
                using var store = new CompressionMetricStore(); using var sink = new MemoryStream();
                var context = Context(sink, mode == "malformed" ? "gzip;q=bad" : "gzip");
                if (mode == "head") context.Request.Method = "HEAD";
                if (mode == "range") context.Request.Headers.Range = "bytes=0-3";
                if (mode == "request-no-transform") context.Request.Headers.CacheControl = "no-transform";
                await Invoke(store, context, async c =>
                {
                    if (mode == "response-no-transform") c.Response.Headers.CacheControl = "no-transform";
                    if (mode == "status") c.Response.StatusCode = 204;
                    if (mode == "encoded") c.Response.Headers.ContentEncoding = "custom";
                    if (mode == "binary") c.Response.ContentType = "application/octet-stream";
                    if (mode == "flush") await c.Response.Body.FlushAsync();
                    if (mode != "empty") await c.Response.Body.WriteAsync(Payload);
                }, minimum: mode == "threshold" ? 65537 : mode == "small" ? 8192 : 0);
                var pair = store.GetSnapshots().Single();
                Check(pair.Key.Encoding == "identity" && pair.Value.InputBytes == sink.Length && pair.Value.CodecDurationTicks == 0, "Exclusion changed bytes or falsely reported compression.");
            }
            using var disabled = new CompressionMetricStore(); using var output = new MemoryStream();
            await Invoke(disabled, Context(output), _ => Task.CompletedTask, enabled: false);
            Check(disabled.GetSnapshots().Count == 0, "Disabled binding emitted measurements.");
        });
        await run("Compression metrics: cancellation and faults record once and preserve destination ownership", async () =>
        {
            foreach (var kind in new[] { "producer", "cancel", "write", "footer", "flush" })
            {
                using var store = new CompressionMetricStore(); using var sink = new FaultSink();
                var context = Context(sink);
                try
                {
                    await Invoke(store, context, async c =>
                    {
                        if (kind == "cancel") throw new OperationCanceledException();
                        if (kind == "write") sink.FailWrite = true;
                        await c.Response.Body.WriteAsync(Payload);
                        if (kind == "producer") throw new IOException("producer");
                        if (kind == "footer") sink.FailWrite = true;
                        if (kind == "flush") { sink.FailFlush = true; await c.Response.Body.FlushAsync(); }
                    }, minimum: 0);
                    throw new Exception("Injected failure did not escape.");
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
                var pair = store.GetSnapshots().Single();
                Check(pair.Value.Responses == 1 && pair.Key.Outcome == (kind == "cancel" ? CompressionMetricOutcome.Cancelled : CompressionMetricOutcome.Faulted), "Failure outcome was missing or duplicated.");
                Check(ReferenceEquals(context.Response.Body, sink) && sink.CanWrite, "Failure changed destination ownership.");
            }
        });
        await run("Compression metrics: codec duration excludes producer time and destination backpressure", async () =>
        {
            var clock = new Clock(); using var store = new CompressionMetricStore(); using var sink = new TimedSink(clock);
            await Invoke(store, Context(sink), async c =>
            {
                clock.Advance(TimeSpan.FromHours(1));
                await c.Response.Body.WriteAsync(Payload);
                clock.Advance(TimeSpan.FromHours(1));
                c.Response.Body.Flush();
                await c.Response.Body.FlushAsync();
            }, minimum: 0, time: clock);
            var stats = store.GetSnapshots().Single().Value;
            Check(sink.WaitTicks > 0 && stats.CodecDurationTicks == 0 && stats.OutputBytes == sink.Length, "Producer or output wait time was reported as codec cost.");
        });
        await run("Compression metrics: exports have bounded dimensions and retire removed routes", () =>
        {
            var publisher = new GatewaySnapshotPublisher();
            publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [new() { RouteId = "route" }] });
            using var store = new CompressionMetricStore(publisher); using var listener = new MeterListener();
            var integers = new Dictionary<string, long>(); double seconds = -1;
            listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "Wafish.Plugin.Compression") owner.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                Check(tags.Length == 3 && tags[0].Value?.ToString() == "route" && tags[1].Value?.ToString() == "gzip" && tags[2].Value?.ToString() == "completed", "Unexpected metric dimensions.");
                Check(instrument.Unit == (instrument.Name.EndsWith("bytes") ? "By" : "{response}"), "Counter unit changed."); integers[instrument.Name] = value;
            });
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => { Check(instrument.Unit == "s", "Duration unit changed."); seconds = value; });
            listener.Start();
            store.Record("route", "gzip", CompressionMetricOutcome.Completed, 100, 20, TimeSpan.FromMilliseconds(50));
            store.Record("unknown", "gzip", CompressionMetricOutcome.Completed, 100, 20, TimeSpan.Zero);
            listener.RecordObservableInstruments();
            Check(integers["wafish.compression.responses"] == 1 && integers["wafish.compression.input_bytes"] == 100 && integers["wafish.compression.output_bytes"] == 20 && seconds == .05, "Exported totals differ from stored values.");
            listener.Dispose();
            publisher.Publish(GatewaySnapshot.Empty with { Version = 2 });
            Check(store.GetSnapshots().Count == 0, "Removed route retained metric series.");
            using var bounded = new CompressionMetricStore();
            Parallel.For(0, 2048, i => bounded.Record("route-" + i, "untrusted-" + i, CompressionMetricOutcome.Completed, 1, 1, TimeSpan.Zero));
            Check(bounded.GetSnapshots().Count == CompressionMetricStore.MaximumSeries && bounded.GetSnapshots().Keys.All(k => k.Encoding == "identity"), "Metric series or encoding dimensions were unbounded.");
            bounded.Record("", "gzip", CompressionMetricOutcome.Completed, 1, 1, TimeSpan.Zero);
            bounded.Record("route", "gzip", (CompressionMetricOutcome)99, 1, 1, TimeSpan.Zero);
            bounded.Dispose(); bounded.Record("route", "gzip", CompressionMetricOutcome.Completed, 1, 1, TimeSpan.Zero);
            Check(bounded.GetSnapshots().Count == 0, "Disposed store retained state.");
            using var concurrent = new CompressionMetricStore();
            Parallel.For(0, 1000, _ => concurrent.Record("route", "br", CompressionMetricOutcome.Cancelled, 10, 5, TimeSpan.FromTicks(2)));
            var totals = concurrent.GetSnapshots().Single().Value;
            Check(totals == new CompressionMetricSnapshot(1000, 10000, 5000, 2000), "Concurrent totals lost updates.");
            return Task.CompletedTask;
        });
        await run("Compression metrics: runtime pipeline uses the host store and streams valid responses", async () =>
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var backend = builder.Build();
            backend.MapGet("/{**path}", async c => { c.Response.ContentType = "text/plain"; await c.Response.Body.WriteAsync(Payload); });
            await backend.StartAsync();
            try
            {
                await using var fixture = await RedirectChecks.Fixture.Start();
                var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
                Check((await routes.TryAddClusterConfig(new() { ClusterId = "metric-backend", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = backend.Urls.Single() } } })).Success, "Cluster creation failed.");
                Check((await routes.TryAddRouteConfig(new() { RouteId = "metric-route", ClusterId = "metric-backend", Match = new() { Path = "/metric" } })).Success, "Route creation failed.");
                await fixture.Bind("metric-route", "compression", "{\"enabled\":true,\"minResponseSize\":0}"); await fixture.WaitRoute("metric-route");
                fixture.Client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
                using var response = await fixture.Client.GetAsync("/metric"); response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadAsByteArrayAsync();
                Check(response.Content.Headers.ContentEncoding.Single() == "gzip", "Runtime omitted compression.");
                var store = fixture.App.Services.GetRequiredService<CompressionMetricStore>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (store.GetSnapshots().Count == 0) await Task.Delay(10, timeout.Token);
                var sample = store.GetSnapshots().Single();
                Check(sample.Key.RouteId == "metric-route" && sample.Value.InputBytes == Payload.Length && sample.Value.OutputBytes == raw.Length, "Runtime used an untracked plugin-local metrics store.");
            }
            finally { await backend.StopAsync(); }
        });
    }
    private static DefaultHttpContext Context(Stream sink, string encoding = "gzip")
    {
        var context = new DefaultHttpContext(); context.Response.Body = sink; context.Request.Headers.AcceptEncoding = encoding;
        context.Response.ContentType = "text/plain";
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(new() { RouteId = "route" }, null!, HttpTransformer.Default) });
        return context;
    }
    private static Task Invoke(CompressionMetricStore store, DefaultHttpContext context, RequestDelegate next, int minimum = 128, bool enabled = true, TimeProvider? time = null)
    {
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [new() { RouteId = "route" }], RoutePlugins =
            ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("route", [new("binding", "compression", PluginBindingScope.Route, "route", JsonSerializer.Serialize(new { enabled, minResponseSize = minimum }), 1, 1, 0)]) });
        var plans = new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
        return new CompressionMiddleware(next, plans, store, time).InvokeAsync(context);
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }
    private sealed class TimedSink(Clock clock) : MemoryStream
    {
        public long WaitTicks;
        private void Wait() { var delay = TimeSpan.FromSeconds(5); clock.Advance(delay); WaitTicks += delay.Ticks; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { Wait(); return base.WriteAsync(buffer, token); }
        public override void Write(byte[] buffer, int offset, int count) { Wait(); base.Write(buffer, offset, count); }
        public override void Flush() { Wait(); base.Flush(); }
        public override Task FlushAsync(CancellationToken token) { Wait(); return base.FlushAsync(token); }
    }
    private sealed class FaultSink : MemoryStream
    {
        public bool FailWrite, FailFlush;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => FailWrite ? ValueTask.FromException(new IOException("write")) : base.WriteAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) { if (FailWrite) throw new IOException("write"); base.Write(buffer, offset, count); }
        public override Task FlushAsync(CancellationToken token) => FailFlush ? Task.FromException(new IOException("flush")) : base.FlushAsync(token);
        public override void Flush() { if (FailFlush) throw new IOException("flush"); base.Flush(); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
