using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Models;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugin.Compression;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class StreamingCompressionChecks
{
    private const string Path = "/api/local-service/stream";
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Streaming compression: explicit flush policy validates before persistence and preserves defaults", async () =>
        {
            var compiler = new CompressionPolicyCompiler();
            CompiledRoutePlugin Compile(CompressionExecutionConfig policy, int version = 1) => compiler.Compile(
                new("id", "compression", PluginBindingScope.Route, "route", JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)), version, 1, 0), new() { RouteId = "route" });
            Check(!((CompressionExecutionConfig)Compile(new()).RuntimeConfig).FlushAfterWrite, "Default flush policy changed.");
            Compile(new() { FlushAfterWrite = true, MinResponseSize = 0 });
            foreach (var minimum in new[] { -1, 1, 1024, 65537 }) Reject(() => Compile(new() { FlushAfterWrite = true, MinResponseSize = minimum }));
            Reject(() => Compile(new() { FlushAfterWrite = true, MinResponseSize = 0 }, 2));
            await using var fixture = await SampleStreamingChecks.Fixture.Start();
            var publisher = fixture.Gateway.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            var repository = fixture.Gateway.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var before = publisher.Current;
            var existing = (await repository.GetBindingsAsync()).Single(b => b.PluginId == "compression");
            foreach (var enabled in new[] { true, false })
            {
                try { await fixture.Gateway.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(new()
                { PluginId = "compression", PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "stream", Enabled = enabled,
                    ConfigJson = "{\"enabled\":true,\"flushAfterWrite\":true,\"minResponseSize\":1}" }); throw new Exception("Invalid flush policy persisted."); }
                catch (ArgumentException) { }
                Check(ReferenceEquals(before, publisher.Current) && (await repository.GetBindingsAsync()).Single(b => b.PluginId == "compression").ConfigJson == existing.ConfigJson, "Rejected flush policy changed active or stored configuration.");
            }
        });
        await run("Streaming compression: real Gzip and Brotli deliver first records before completion and stop on disconnect", async () =>
        {
            foreach (var encoding in new[] { "gzip", "br" })
            {
                await using var fixture = await SampleStreamingChecks.Fixture.Start(allowTransform: true, flushAfterWrite: true);
                using var client = fixture.Client(encoding);
                for (var request = 0; request < 2; request++)
                {
                    using var complete = await client.GetAsync(Path + "?chunks=3&intervalMilliseconds=10"); complete.EnsureSuccessStatusCode();
                    Check(complete.Content.Headers.ContentEncoding.Single() == encoding, "Complete response was not compressed.");
                    using var decoded = Decode(await complete.Content.ReadAsStreamAsync(), encoding);
                    using var lines = new StreamReader(decoded);
                    var records = (await lines.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Check(records.Length == 3 && records[2].Contains("\"sequence\":3"), "Compressed trailer or framing was corrupted.");
                }
                Check(fixture.Requests == 2, "No-store response entered the cache.");
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var response = await client.GetAsync(Path + "?chunks=20&intervalMilliseconds=500", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                Check(response.Content.Headers.ContentEncoding.Single() == encoding && response.Content.Headers.ContentLength is null, "Streaming response lost encoding or gained a complete length.");
                using var body = Decode(await response.Content.ReadAsStreamAsync(cancellation.Token), encoding);
                using var reader = new StreamReader(body);
                Check((await reader.ReadLineAsync(cancellation.Token))?.Contains("\"sequence\":1") == true && fixture.Log.Ended.Count == 2,
                    "Compressed first record waited for backend completion: " + encoding);
                cancellation.Cancel(); response.Dispose();
                await Until(() => fixture.Log.Ended.Count == 3);
                Check(fixture.Log.Ended.Last() is { Cancelled: true, Chunks: < 20 } && fixture.Requests == 3, "Disconnect retained or replayed compressed work.");
            }
        });
        await run("Streaming compression: no-transform overrides explicit flush policy and retry deadlines never replay started codecs", async () =>
        {
            foreach (var responseDirective in new[] { true, false })
            {
                await using var fixture = await SampleStreamingChecks.Fixture.Start(allowTransform: !responseDirective, flushAfterWrite: true);
                using var client = fixture.Client("gzip");
                if (!responseDirective) client.DefaultRequestHeaders.CacheControl = new() { NoTransform = true };
                using var response = await client.GetAsync(Path + "?chunks=2&intervalMilliseconds=0");
                Check(response.Content.Headers.ContentEncoding.Count == 0 && (await response.Content.ReadAsStringAsync()).Contains("\"sequence\":2"), "Explicit flush ignored no-transform.");
            }
            await using var expired = await SampleStreamingChecks.Fixture.Start(timeout: 1, allowTransform: true, flushAfterWrite: true);
            using var deadlineClient = expired.Client("gzip");
            using var interrupted = await deadlineClient.GetAsync(Path + "?chunks=20&intervalMilliseconds=500", HttpCompletionOption.ResponseHeadersRead);
            using var stream = Decode(await interrupted.Content.ReadAsStreamAsync(), "gzip"); using var reader = new StreamReader(stream);
            Check((await reader.ReadLineAsync())?.Contains("\"sequence\":1") == true, "Deadline response did not deliver an initial compressed record.");
            try { await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expired compressed response reported completion."); } catch (IOException) { }
            await Until(() => expired.Log.Ended.Count == 1);
            Check(expired.Requests == 1 && expired.Log.Ended.Single().Cancelled, "Deadline replayed a started codec.");
        });
        await run("Streaming compression: flush work, failure ownership, and diagnostic size and throughput costs remain bounded", async () =>
        {
            var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"message\":\"synthetic repeated record\"}\n", 24)));
            foreach (var encoding in new[] { "gzip", "br" })
            foreach (var flush in new[] { false, true })
            {
                await Measure(encoding, flush, 64, false);
                await Measure(encoding, flush, 2048, true);
            }
            foreach (var cancellation in new[] { false, true })
            {
                using var cancel = new CancellationTokenSource();
                using var sink = new Sink { OnFlush = () => { if (cancellation) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); } throw new IOException("flush failed"); } };
                var context = Context(sink, "gzip"); context.RequestAborted = cancel.Token;
                using var metrics = new CompressionMetricStore();
                try { await Middleware(true, "gzip", async c => await c.Response.Body.WriteAsync(payload, c.RequestAborted), metrics).InvokeAsync(context); throw new Exception("Failed flush was ignored."); }
                catch (OperationCanceledException) when (cancellation) { }
                catch (IOException) when (!cancellation) { }
                Check(ReferenceEquals(context.Response.Body, sink) && metrics.GetSnapshots().Single().Key.Outcome ==
                    (cancellation ? CompressionMetricOutcome.Cancelled : CompressionMetricOutcome.Faulted), "Failed flush lost body ownership or outcome.");
            }
            async Task Measure(string encoding, bool flush, int count, bool report)
            {
                using var sink = new Sink(); var context = Context(sink, encoding);
                var middleware = Middleware(flush, encoding, async c => { for (var i = 0; i < count; i++) await c.Response.Body.WriteAsync(payload); });
                var before = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
                await middleware.InvokeAsync(context); timer.Stop();
                var allocated = GC.GetTotalAllocatedBytes(true) - before;
                Check(flush ? sink.Flushes >= count : sink.Flushes == 0, "Flush policy changed default or omitted opted-in writes.");
                Check(allocated < 4 * 1024 * 1024 && sink.Bytes > 0, "Codec flush retained unbounded managed state.");
                if (report) Console.WriteLine($"Streaming codec diagnostic: encoding={encoding}, flush={flush}, input={count * payload.Length}, output={sink.Bytes}, elapsedMs={timer.Elapsed.TotalMilliseconds:F2}, MiBPerSecond={count * payload.Length / 1048576d / timer.Elapsed.TotalSeconds:F2}, allocated={allocated}.");
            }
        });
    }
    private static Stream Decode(Stream stream, string encoding) => encoding == "gzip" ? new GZipStream(stream, CompressionMode.Decompress) : new BrotliStream(stream, CompressionMode.Decompress);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid flush configuration accepted."); }
    private static async Task Until(Func<bool> condition) { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, limit.Token); }
    private static DefaultHttpContext Context(Stream sink, string encoding)
    {
        var context = new DefaultHttpContext(); context.Response.Body = sink; context.Request.Headers.AcceptEncoding = encoding;
        context.Response.ContentType = "application/json";
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(new() { RouteId = "flush" }, null!, HttpTransformer.Default) });
        return context;
    }
    private static CompressionMiddleware Middleware(bool flush, string encoding, RequestDelegate next, CompressionMetricStore? metrics = null)
    {
        var publisher = new GatewaySnapshotPublisher(); var route = new RouteConfig { RouteId = "flush" };
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route], RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("flush",
            [new("binding", "compression", PluginBindingScope.Route, "flush", JsonSerializer.Serialize(new { enabled = true, minResponseSize = 0, flushAfterWrite = flush, allowedEncodings = new[] { encoding } }), 1, 1, 0)]) });
        return new(next, new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance), metrics);
    }
    private sealed class Sink : Stream
    {
        public long Bytes; public int Flushes; public Action? OnFlush;
        public override void Write(byte[] buffer, int offset, int count) => Bytes += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Bytes += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Bytes += buffer.Length; return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() { Flushes++; OnFlush?.Invoke(); }
        public override Task FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Bytes; public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
