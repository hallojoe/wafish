using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SampleLocalService;
using SampleLocalService.Controllers;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

internal static class SampleStreamingChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Sample streaming: bounded inputs, record framing, and identity survive HTTP", async () =>
        {
            await using var fixture = await Fixture.Start();
            using var client = new HttpClient { BaseAddress = new(fixture.Backend.Urls.Single()) };
            foreach (var query in new[] { "chunks=0", "chunks=101", "chunks=-1", "chunks=1.5", "chunks=bad",
                "intervalMilliseconds=-1", "intervalMilliseconds=1001", "chunks=100&intervalMilliseconds=1000" })
            {
                using var invalid = await client.GetAsync(Path + "?" + query);
                Check(invalid.StatusCode == HttpStatusCode.BadRequest && invalid.Content.Headers.ContentType?.MediaType == "application/problem+json", "Invalid stream bounds did not return a problem response.");
            }
            foreach (var (query, count) in new[] { ("", 5), ("?chunks=100&intervalMilliseconds=0", 100), ("?chunks=1&intervalMilliseconds=1000", 1) })
            {
                using var response = await client.GetAsync(Path + query); response.EnsureSuccessStatusCode();
                Check(response.Content.Headers.ContentType?.MediaType == "application/x-ndjson" && response.Headers.CacheControl?.NoStore == true, "Stream headers changed.");
                var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Check(lines.Length == count && response.Headers.GetValues("X-Backend-Instance").Single() == "stream-backend", "Record count or identity header changed.");
                for (var i = 0; i < lines.Length; i++)
                {
                    var record = JsonSerializer.Deserialize<SampleStreamResponse>(lines[i], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    Check(record.Sequence == i + 1 && record.Instance == "stream-backend" && !string.IsNullOrEmpty(record.TraceId), "Stream record lost sequence, identity, or tracing.");
                }
            }
            Check(fixture.Log.Ended.Count == 3 && fixture.Log.Ended.All(item => !item.Cancelled), "Normal streams did not complete cleanly.");
        });
        await run("Sample streaming: cache, compression, and retry bindings preserve early delivery and disconnect cancellation", async () =>
        {
            foreach (var accept in new[] { "identity", "gzip", "br" })
            {
                await using var fixture = await Fixture.Start();
                using var client = fixture.Client(accept);
                for (var i = 0; i < 2; i++)
                {
                    using var response = await client.GetAsync(Path + "?chunks=2&intervalMilliseconds=0"); response.EnsureSuccessStatusCode();
                    using var decoded = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(decoded);
                    Check((await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "Policy pipeline corrupted framing.");
                }
                Check(fixture.Requests == 2, "No-store stream entered the response cache.");
                var prior = fixture.Log.Ended.Count;
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var streaming = await client.GetAsync(Path + "?chunks=20&intervalMilliseconds=500", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                Check(streaming.Content.Headers.ContentLength is null && streaming.Headers.CacheControl?.NoStore == true, "Stream became a buffered response.");
                Check(streaming.Content.Headers.ContentEncoding.Count == 0 && streaming.Headers.CacheControl?.NoTransform == true, "No-transform stream was compressed.");
                using var body = await streaming.Content.ReadAsStreamAsync(cancellation.Token);
                using var lines = new StreamReader(body);
                var firstLine = await lines.ReadLineAsync(cancellation.Token);
                Check(firstLine?.Contains("\"sequence\":1") == true && fixture.Log.Ended.Count == prior,
                    $"First record arrived only after backend completion: {accept}, before={prior}, after={fixture.Log.Ended.Count}, line={firstLine}.");
                cancellation.Cancel(); streaming.Dispose();
                await Until(() => fixture.Log.Ended.Count == prior + 1);
                var ended = fixture.Log.Ended.Last();
                Check(ended.Cancelled && ended.Chunks < 20 && fixture.Requests == 3, "Disconnect retained backend work or replayed the stream.");
            }
        });
        await run("Sample streaming: retry deadline aborts a started response without replay", async () =>
        {
            await using var fixture = await Fixture.Start(timeout: 1);
            using var client = fixture.Client("identity");
            using var response = await client.GetAsync(Path + "?chunks=20&intervalMilliseconds=500", HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Check((await reader.ReadLineAsync())?.Contains("\"sequence\":1") == true, "Deadline test did not start streaming.");
            try { await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expired response was reported complete."); }
            catch (IOException) { }
            await Until(() => fixture.Log.Ended.Count == 1);
            Check(fixture.Requests == 1 && fixture.Log.Ended.Single() is { Cancelled: true, Chunks: < 20 }, "Retry deadline replayed a partial stream or retained backend work.");
        });
    }
    private const string Path = "/api/local-service/stream";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    internal sealed class StreamLog : ILoggerProvider, ILogger
    {
        public readonly ConcurrentQueue<(int Chunks, bool Cancelled)> Ended = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (id.Id != 5201 || state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(); Ended.Enqueue(((int)fields["Chunks"]!, (bool)fields["Cancelled"]!));
        }
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication Backend { get; init; }
        public required RedirectChecks.Fixture Gateway { get; init; }
        public required StreamLog Log { get; init; }
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public HttpClient Client(string encoding)
        {
            var client = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = Gateway.Client.BaseAddress };
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd(encoding); return client;
        }
        public static async Task<Fixture> Start(int timeout = 30, bool allowTransform = false, bool flushAfterWrite = false)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var log = new StreamLog(); builder.Logging.ClearProviders(); builder.Logging.AddProvider(log);
            var identity = new SampleInstanceIdentity("stream-backend"); builder.Services.AddSingleton(identity);
            builder.Services.AddControllers().AddApplicationPart(typeof(LocalServiceController).Assembly);
            var backend = builder.Build();
            RedirectChecks.Fixture? gateway = null;
            try
            {
                gateway = await RedirectChecks.Fixture.Start();
                var fixture = new Fixture { Backend = backend, Gateway = gateway, Log = log };
                backend.Use(async (context, next) => { Interlocked.Increment(ref fixture._requests); identity.Apply(context);
                    if (allowTransform) context.Response.OnStarting(() => { context.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
                    await next(context); });
                backend.MapControllers(); await backend.StartAsync();
                var routes = gateway.App.Services.GetRequiredService<IDynamicYarpConfigService>();
                Check((await routes.TryAddClusterConfig(new() { ClusterId = "stream", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = backend.Urls.Single() } } })).Success, "Stream cluster failed.");
                Check((await routes.TryAddRouteConfig(new() { RouteId = "stream", ClusterId = "stream", Match = new() { Path = Path } })).Success, "Stream route failed.");
                await gateway.Bind("stream", "response-cache", "{\"enabled\":true,\"fillWaitMilliseconds\":0}");
                await gateway.Bind("stream", "compression", JsonSerializer.Serialize(new { enabled = true, minResponseSize = 0, flushAfterWrite, mimeTypes = new[] { "application/x-ndjson" } }));
                await gateway.Bind("stream", "request-retry", JsonSerializer.Serialize(new { enabled = true, maxRetries = 2, timeoutSeconds = timeout, totalTimeoutSeconds = timeout }));
                await gateway.WaitRoute("stream"); return fixture;
            }
            catch { if (gateway is not null) await gateway.DisposeAsync(); await backend.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Gateway.DisposeAsync(); await Backend.StopAsync(); await Backend.DisposeAsync(); }
    }
}
