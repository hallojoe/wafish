using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.Infrastructure.Resilience;
using Wafish.Infrastructure.Middleware;
using Wafish.Models;
using Wafish.Plugin.Cache;
using Wafish.Plugin.Compression;
using Wafish.Plugin.Retry;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class RetryStreamingChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Retry: complete replay, unknown and oversized bodies, truncation, and cancellation", async () =>
        {
            foreach (var size in new[] { 0, 17, RequestRetryMiddleware.MaxRetryBodySizeBytes, RequestRetryMiddleware.MaxRetryBodySizeBytes + 1 })
            {
                foreach (var known in new[] { true, false })
            {
                var f = new Fixture(); var bodies = new List<byte[]>();
                f.Respond = async c =>
                {
                    using var read = new MemoryStream(); await c.Request.Body.CopyToAsync(read, c.RequestAborted);
                    bodies.Add(read.ToArray()); c.Response.StatusCode = f.Calls == 1 ? 503 : 200;
                    await c.Response.Body.WriteAsync("response"u8.ToArray(), c.RequestAborted);
                };
                var body = Enumerable.Repeat((byte)42, size).ToArray();
                var context = f.Context(); var original = new ShortBody(body);
                context.Request.Body = original; context.Request.ContentLength = known ? size : null;
                await f.Pipeline(context);
                var expected = known && size <= RequestRetryMiddleware.MaxRetryBodySizeBytes ? 2 : 1;
                Require(f.Calls == expected && bodies.All(b => b.SequenceEqual(body)), "Replay count or content changed.");
                Require(ReferenceEquals(context.Request.Body, original), "Request stream was not restored.");
            }
            }

            var truncated = new Fixture(); var shortContext = truncated.Context();
            shortContext.Request.Body = new ShortBody([1, 2]); shortContext.Request.ContentLength = 10;
            try { await truncated.Pipeline(shortContext); throw new Exception("Truncated body accepted."); }
            catch (BadHttpRequestException ex) { Require(ex.StatusCode == 400 && truncated.Calls == 0, "Truncated body reached upstream."); }
            var cancelled = new Fixture(); var cancelContext = cancelled.Context();
            cancelContext.RequestAborted = new CancellationToken(true);
            try { await cancelled.Pipeline(cancelContext); throw new Exception("Cancellation swallowed."); }
            catch (OperationCanceledException) { Require(cancelled.Calls == 0, "Cancelled request reached upstream."); }
        });
        await run("Retry: request preparation observes its deadline and restores ownership", async () =>
        {
            var f = new Fixture(); var context = f.Context();
            var original = new StalledBody(); context.Request.Body = original; context.Request.ContentLength = 1;
            try { await f.Pipeline(context).WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Preparation deadline ignored."); }
            catch (OperationCanceledException) { }
            Require(f.Calls == 0 && ReferenceEquals(context.Request.Body, original), "Timed-out preparation leaked ownership.");
        });
        await run("Retry: uncommitted exceptions retry; exhausted destinations return 503", async () =>
        {
            var f = new Fixture(); f.Respond = c => f.Calls == 1 ? throw new IOException("Injected") : c.Response.Body.WriteAsync("ok"u8.ToArray()).AsTask();
            var context = f.Context(); await f.Pipeline(context);
            Require(f.Calls == 2 && Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()) == "ok", "Exception retry failed.");
            var unavailable = new Fixture { Coordinator = new Candidates(false) };
            var missing = unavailable.Context(); await unavailable.BuildPipeline()(missing);
            Require(missing.Response.StatusCode == 503 && unavailable.Calls == 0, "No destinations returned success.");
        });
        await run("Kestrel: retry headers, overflow, flush, start, completion, and body writer", async () =>
        {
            foreach (var mode in new[] { "retry", "overflow", "flush", "start", "complete", "sync", "array", "file", "writer", "unflushed-writer", "disable", "exception" })
            {
                var f = new Fixture();
                f.Respond = async c =>
                {
                    c.Response.StatusCode = f.Calls == 1 ? 503 : 200;
                    c.Response.Headers["X-Attempt"] = f.Calls.ToString();
                    if (mode == "overflow") { await c.Response.Body.WriteAsync(new byte[128 * 1024], c.RequestAborted); return; }
                    if (mode == "sync") { c.Response.Body.Write("body"u8.ToArray(), 0, 4); c.Response.Body.Flush(); return; }
                    if (mode == "array") { await c.Response.Body.WriteAsync("body"u8.ToArray(), 0, 4, c.RequestAborted); await c.Response.Body.FlushAsync(); return; }
                    if (mode == "file")
                    {
                        var path = Path.GetTempFileName();
                        try { await File.WriteAllTextAsync(path, "body"); await c.Response.SendFileAsync(path, c.RequestAborted); }
                        finally { File.Delete(path); }
                        return;
                    }
                    if (mode == "writer") { await c.Response.BodyWriter.WriteAsync("body"u8.ToArray(), c.RequestAborted); return; }
                    if (mode == "unflushed-writer")
                    {
                        "body"u8.CopyTo(c.Response.BodyWriter.GetSpan(4));
                        c.Response.BodyWriter.Advance(4); return;
                    }
                    if (mode == "start")
                    {
                        await c.Response.StartAsync(c.RequestAborted);
                    }

                    await c.Response.Body.WriteAsync("body"u8.ToArray(), c.RequestAborted);
                    if (mode == "flush" || mode == "exception")
                    {
                        await c.Response.Body.FlushAsync(c.RequestAborted);
                    }

                    if (mode == "complete")
                    {
                        await c.Response.CompleteAsync();
                    }

                    if (mode == "disable") { c.Features.Get<IHttpResponseBodyFeature>()!.DisableBuffering(); await c.Response.Body.WriteAsync("tail"u8.ToArray()); }
                    if (mode == "exception")
                    {
                        throw new IOException("Injected after output");
                    }
                };
                await using var app = await Start(f);
                using var client = Client(app);
                if (mode == "exception")
                {
                    try { await client.GetByteArrayAsync("/"); } catch (HttpRequestException) { }
                    Require(f.Calls == 1, "Exception after output retried.");
                }
                else
                {
                    using var response = await client.GetAsync("/");
                    var body = await response.Content.ReadAsByteArrayAsync();
                    Require(f.Calls == (mode == "retry" ? 2 : 1), mode + " attempt count");
                    Require(response.Headers.GetValues("X-Attempt").Single() == f.Calls.ToString(), "Stale headers survived.");
                    Require(body.Length == (mode == "overflow" ? 128 * 1024 : mode == "disable" ? 8 : 4), mode + " body changed");
                    if (mode == "retry")
                    {
                        Require(response.Headers.GetValues("X-Retry-Count").Single() == "1", "Retry header was added too late.");
                    }
                }
                await app.StopAsync();
            }
        });
        await run("Kestrel: cache, compression, and retry stream before completion and release cancelled captures", async () =>
        {
            foreach (var compress in new[] { false, true })
            {
                var f = new Fixture(cache: true, compress: compress);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Respond = async c =>
                {
                    c.Response.ContentType = "text/plain";
                    c.Response.Headers.CacheControl = "public,max-age=60";
                    var payload = new byte[96 * 1024]; Random.Shared.NextBytes(payload);
                    try
                    {
                        await c.Response.Body.WriteAsync(payload, c.RequestAborted);
                        await c.Response.Body.FlushAsync(c.RequestAborted);
                        await release.Task.WaitAsync(c.RequestAborted);
                        await c.Response.Body.WriteAsync(payload, c.RequestAborted);
                    }
                    finally { stopped.TrySetResult(); }
                };
                await using var app = await Start(f); using var client = Client(app);
                if (compress)
                {
                    client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                var first = new byte[256];
                Require(await body.ReadAsync(first, timeout.Token) > 0 && !release.Task.IsCompleted, "First bytes waited for completion.");
                Require(f.Store.AllocatedBytes <= f.Store.SizeLimitBytes, "Capture exceeded its shared budget.");
                response.Dispose();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                release.TrySetResult();
                await app.StopAsync();
                Require(f.Store.AllocatedBytes == 0, "Cancelled capture retained memory.");
                Require(f.Calls == 1, "Disconnect retried upstream.");
            }
        });
        await run("Kestrel and YARP: failed upstream response retries with an intact request body", async () =>
        {
            var upstreamBuilder = WebApplication.CreateBuilder(); upstreamBuilder.Logging.ClearProviders();
            upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var upstream = upstreamBuilder.Build();
            var bodies = new List<string>();
            upstream.Run(async c =>
            {
                using var reader = new StreamReader(c.Request.Body);
                bodies.Add(await reader.ReadToEndAsync(c.RequestAborted));
                if (bodies.Count == 1)
                {
                    c.Response.StatusCode = 503;
                }
                else
                {
                    await c.Response.WriteAsync("forwarded", c.RequestAborted);
                }
            });
            await upstream.StartAsync();
            var upstreamAddress = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var services = new ServiceCollection().AddLogging().AddHttpForwarder().BuildServiceProvider();
            var forwarder = services.GetRequiredService<IHttpForwarder>();
            using var invoker = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            var f = new Fixture();
            f.Respond = async c => { await forwarder.SendAsync(c, upstreamAddress, invoker, new ForwarderRequestConfig(), HttpTransformer.Default); };
            await using var gateway = await Start(f); using var client = Client(gateway);
            using var response = await client.PostAsync("/echo", new StringContent("complete request body"));
            Require(response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "forwarded", "Forwarding did not recover.");
            Require(bodies.Count == 2 && bodies.All(b => b == "complete request body"), "YARP replay changed request bytes.");
            await gateway.StopAsync(); await upstream.StopAsync();
        });
        await run("Kestrel: combined retry, compression, and cache preserve successful bytes", async () =>
        {
            var f = new Fixture(cache: true, compress: true);
            var expected = Encoding.UTF8.GetBytes(new string('a', 20000));
            f.Respond = async c =>
            {
                c.Response.ContentType = "text/plain"; c.Response.Headers.CacheControl = "public,max-age=60";
                if (f.Calls == 1) { c.Response.StatusCode = 503; await c.Response.Body.WriteAsync("failure"u8.ToArray()); }
                else { c.Response.ContentType = "text/plain"; c.Response.Headers.CacheControl = "public,max-age=60"; await c.Response.Body.WriteAsync(expected); }
            };
            await using var app = await Start(f); using var client = Client(app);
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
            for (var i = 0; i < 2; i++)
            {
                using var response = await client.GetAsync("/cached"); response.EnsureSuccessStatusCode();
                Require(response.Content.Headers.ContentEncoding.Single() == "gzip", "Compression missing.");
                await using var compressed = await response.Content.ReadAsStreamAsync();
                await using var decoder = new GZipStream(compressed, CompressionMode.Decompress);
                using var output = new MemoryStream(); await decoder.CopyToAsync(output);
                Require(output.ToArray().SequenceEqual(expected), "Failure bytes leaked into the response.");
            }
            Require(f.Calls == 2, "Successful response was not cached.");
            await app.StopAsync();
        });
    }
    private static async Task<WebApplication> Start(Fixture fixture)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async c => { fixture.SetRoute(c); await fixture.Pipeline(c); });
        await app.StartAsync(); return app;
    }
    private static HttpClient Client(WebApplication app) => new(new SocketsHttpHandler { MaxResponseDrainSize = 0 })
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
        Timeout = TimeSpan.FromSeconds(10)
    };
    private sealed class Fixture
    {
        private readonly RouteConfig _route = new() { RouteId = "review", ClusterId = "cluster", Match = new() { Path = "/{**path}" } };
        private readonly GatewayPluginExecutionPlanProvider _plans;
        private readonly bool _cache, _compress;
        public int Calls;
        public Func<HttpContext, Task> Respond = c => Task.CompletedTask;
        public IDestinationCandidateCoordinator Coordinator = new Candidates(true);
        public ResponseCacheStore Store { get; } = new(Options.Create(new ResponseCacheBudgetOptions { SizeLimitBytes = 512 * 1024 }));
        public RequestDelegate Pipeline { get; }
        public Fixture(bool cache = false, bool compress = false)
        {
            _cache = cache; _compress = compress;
            var publisher = new GatewaySnapshotPublisher();
            publisher.Publish(GatewaySnapshot.Empty with
            {
                Version = 1, Routes = [_route],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("review",
                    [new("retry", "request-retry", PluginBindingScope.Route, "review", "{\"enabled\":true,\"maxRetries\":1,\"timeoutSeconds\":1,\"backoffBaseMs\":0,\"backoffJitterMs\":0,\"retryNonIdempotent\":true}", 1, 1, 400),
                     new("cache", "response-cache", PluginBindingScope.Route, "review", "{\"enabled\":true,\"maxBodyBytes\":65536}", 1, 1, 300),
                     new("compression", "compression", PluginBindingScope.Route, "review", "{\"enabled\":true,\"minResponseSize\":1024}", 1, 1, 350)])
            });
            _plans = new(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            Pipeline = BuildPipeline();
        }
        public RequestDelegate BuildPipeline()
        {
            var retry = new RequestRetryMiddleware(async c => { Interlocked.Increment(ref Calls); await Respond(c); },
                NullLogger<RequestRetryMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!,
                new RecyclableMemoryStreamManager(), new InMemoryCircuitStateStore(), Coordinator, _plans);
            RequestDelegate pipeline = retry.InvokeAsync;
            if (_compress)
            {
                pipeline = new CompressionMiddleware(pipeline, _plans).InvokeAsync;
            }

            if (_cache)
            {
                pipeline = new ResponseCacheMiddleware(pipeline, Store, _plans).InvokeAsync;
            }

            return pipeline;
        }
        public void SetRoute(HttpContext c) => c.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(_route, null!, HttpTransformer.Default) });
        public DefaultHttpContext Context()
        {
            var c = new DefaultHttpContext(); c.Request.Path = "/review"; c.Request.Method = "GET";
            c.Response.Body = new MemoryStream(); c.Request.Body = Stream.Null; SetRoute(c); return c;
        }
    }
    private sealed class Candidates(bool available) : IDestinationCandidateCoordinator
    {
        public ValueTask<IReadOnlyList<DestinationState>> ApplyAsync(HttpContext context, bool excludeAttempted, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<DestinationState>>(available ? [new DestinationState("one")] : []);
        public void MarkAttempted(HttpContext context, DestinationState? destination) { }
    }
    private sealed class StalledBody : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
    }
    private sealed class ShortBody(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 37)], cancellationToken);
    }
    private static void Require(bool value, string message) { if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}
