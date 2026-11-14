using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Wafish.Models;
using Wafish.Plugin.Cache;
using Wafish.Plugin.Compression;
using Wafish.Services;
using Wafish.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class ResponseStreamingChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Cache: host, query, response Vary, GET/HEAD, and configuration generations", async () =>
        {
            var f = new Fixture();
            var a = await f.Send();
            Check(Text(a) == "body-1", "initial body");
            Check(Text(await f.Send()) == "body-1" && f.Calls == 1, "cache hit");
            var head = await f.Send(c => c.Request.Method = "HEAD");
            Check(Text(head) == "" && head.Response.ContentLength == 6 && f.Calls == 1, "HEAD hit");
            await f.Send(c => c.Request.Host = new("other.test"));
            await f.Send(c => c.Request.QueryString = new("?a=1"));
            await f.Send(c => c.Request.Headers.AcceptLanguage = "fr-FR");
            Check(f.Calls == 4, "variant separation");
            await f.Send(c => { c.Request.Path = "/head"; c.Request.Method = "HEAD"; });
            Check(Text(await f.Send(c => c.Request.Path = "/head")) == "body-6", "HEAD miss does not populate GET");
            f.Publish(2);
            Check(Text(await f.Send()) == "body-7", "generation invalidation");
        });
        await run("Cache: directives, freshness, malformed metadata, and conditional requests", async () =>
        {
            foreach (var directive in new[] { "no-cache", "no-store", "max-age=0" })
            {
                var f = new Fixture(); await f.Send();
                await f.Send(c => c.Request.Headers.CacheControl = directive);
                Check(f.Calls == 2, directive);
            }
            foreach (var header in new[] { "Range", "If-None-Match", "If-Modified-Since" })
            {
                var f = new Fixture(); await f.Send();
                await f.Send(c => c.Request.Headers[header] = "test");
                Check(f.Calls == 2, header);
            }
            var miss = new Fixture();
            Check((await miss.Send(c => c.Request.Headers.CacheControl = "only-if-cached")).Response.StatusCode == 504 && miss.Calls == 0, "only-if-cached");
            foreach (var (header, value) in new[] { ("Cache-Control", "public, max-age=0"), ("Cache-Control", "public, s-maxage=0, max-age=60"),
                ("Vary", "*"), ("Age", "120"), ("Age", "bad"), ("Expires", "bad"), ("Date", "bad") })
            {
                var f = new Fixture { Respond = async c => { c.Response.Headers[header] = value; await c.Response.Body.WriteAsync("fresh"u8.ToArray()); } };
                await f.Send(); await f.Send(); Check(f.Calls == 2, $"reject {header}: {value}");
            }
            var aged = new Fixture { Respond = async c => { c.Response.Headers.Age = "10"; await c.Response.Body.WriteAsync("aged"u8.ToArray()); } };
            await aged.Send();
            Check(long.Parse((await aged.Send()).Response.Headers.Age.ToString()) >= 10, "Age preserved");
            await aged.Send(c => c.Request.Headers.CacheControl = "max-age=5");
            Check(aged.Calls == 2, "request freshness bound");
        });
        await run("Cache: overflow streams intact and concurrent captures stay within budget", async () =>
        {
            var f = new Fixture(capacity: 1024, budget: 68000);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MemoryStream? firstOutput = null;
            f.Respond = async c =>
            {
                await c.Response.Body.WriteAsync(new byte[512]);
                if (c.Request.Path == "/first") { Check(firstOutput!.Length == 512, "first bytes streamed"); entered.SetResult(); await release.Task; }
                await c.Response.Body.WriteAsync(new byte[512]);
            };
            var first = f.Send(c => { c.Request.Path = "/first"; firstOutput = (MemoryStream)c.Response.Body; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => f.Send(c => c.Request.Path = "/parallel" + i)));
            Check(concurrent.All(c => ((MemoryStream)c.Response.Body).Length == 1024), "concurrent bodies");
            Check(f.Store.AllocatedBytes <= f.Store.SizeLimitBytes, "capture budget");
            release.SetResult(); await first;
            f.Respond = async c => { for (var i = 0; i < 256; i++) { await c.Response.Body.WriteAsync(new byte[4096]); } };
            var large = await f.Send(c => c.Request.Path = "/large");
            Check(((MemoryStream)large.Response.Body).Length == 1024 * 1024, "overflow body intact");
            Check(f.Store.AllocatedBytes == 0, "overflow releases budget and evicted entry");
            using var cancelled = new CancellationTokenSource();
            f.Respond = c => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); };
            try { await f.Send(c => c.RequestAborted = cancelled.Token); throw new Exception("cancellation swallowed"); }
            catch (OperationCanceledException) { }
            Check(f.Store.AllocatedBytes == 0, "cancellation releases capture");
        });
        await run("Compression: exact negotiation, streaming, cache variants, and exclusions", async () =>
        {
            var body = Encoding.UTF8.GetBytes(new string('a', 20000));
            var f = new Fixture(compress: true) { Respond = c => c.Response.Body.WriteAsync(body).AsTask() };
            foreach (var (accept, expected) in new[] { ("br;q=0.2, gzip;q=0.8", "gzip"), ("br;q=0, *;q=1", "gzip"),
                ("gzipfoo", ""), ("br;q=0,gzip;q=0", ""), ("br", "br"), ("gzip;q=invalid", "") })
            {
                var calls = f.Calls;
                for (var i = 0; i < 2; i++)
                {
                    var result = await f.Send(c => c.Request.Headers.AcceptEncoding = accept);
                    Check(result.Response.Headers.ContentEncoding.ToString() == expected, accept);
                    Check(Decode(result).SequenceEqual(body), "decoded body");
                    Check(result.Response.Headers.Vary.ToString().Contains("Accept-Encoding"), "Vary");
                }
                Check(f.Calls == calls + 1, "compressed cache hit");
            }
            foreach (var mode in new[] { "range", "encoded", "no-transform", "204", "205", "304", "HEAD" })
            {
                var excluded = new Fixture(compress: true) { Respond = async c =>
                {
                    if (mode == "encoded")
                    {
                        c.Response.Headers.ContentEncoding = "custom";
                    }

                    if (mode == "no-transform")
                    {
                        c.Response.Headers.CacheControl = "public, no-transform";
                    }

                    if (int.TryParse(mode, out var status))
                    {
                        c.Response.StatusCode = status;
                    }

                    await c.Response.Body.WriteAsync(body);
                } };
                var result = await excluded.Send(c => { c.Request.Headers.AcceptEncoding = "gzip"; if (mode == "range") { c.Request.Headers.Range = "bytes=0-100"; } if (mode == "HEAD") { c.Request.Method = "HEAD"; } });
                Check(result.Response.Headers.ContentEncoding.ToString() == (mode == "encoded" ? "custom" : ""), mode);
            }
            MemoryStream? output = null;
            var streaming = new Fixture(compress: true) { Respond = async c =>
            {
                await c.Response.Body.WriteAsync(body);
                await c.Response.Body.FlushAsync();
                Check(output!.Length > 0, "compressed bytes before completion");
                await c.Response.Body.WriteAsync(body);
            } };
            var response = await streaming.Send(c => { c.Request.Headers.AcceptEncoding = "gzip"; output = (MemoryStream)c.Response.Body; });
            Check(Decode(response).Length == body.Length * 2, "stream complete");
            var flush = new Fixture(compress: true) { Respond = async c => { await c.Response.Body.WriteAsync("a"u8.ToArray()); await c.Response.Body.FlushAsync(); await c.Response.Body.WriteAsync(body); } };
            Check((await flush.Send(c => c.Request.Headers.AcceptEncoding = "gzip")).Response.Headers.ContentEncoding.Count == 0, "early flush passthrough");
        });
    }
    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
    private static string Text(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    private static byte[] Decode(HttpContext context)
    {
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        using var input = new MemoryStream(bytes);
        using Stream decoder = context.Response.Headers.ContentEncoding.ToString() switch { "br" => new BrotliStream(input, CompressionMode.Decompress), "gzip" => new GZipStream(input, CompressionMode.Decompress), _ => input };
        using var output = new MemoryStream(); decoder.CopyTo(output); return output.ToArray();
    }
    internal sealed class Fixture
    {
        public ResponseCacheStore Store { get; }
        public int Calls;
        public Func<HttpContext, Task>? Respond;
        private readonly GatewaySnapshotPublisher _publisher = new();
        private readonly RouteConfig _route = new() { RouteId = "test", Match = new() { Path = "/{**path}" } };
        private readonly ResponseCacheMiddleware _middleware;
        private readonly int _capacity;
        private readonly bool _compress;
        public Fixture(int capacity = 65536, long budget = 1024 * 1024, bool compress = false)
        {
            _capacity = capacity; _compress = compress;
            Store = new(Options.Create(new ResponseCacheBudgetOptions { SizeLimitBytes = budget }));
            Publish(1);
            var plans = new GatewayPluginExecutionPlanProvider(_publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            RequestDelegate upstream = async c => { var calls = Interlocked.Increment(ref Calls); c.Response.ContentType = "text/plain"; c.Response.Headers.CacheControl = "public, max-age=60"; c.Response.Headers.Append("Vary", "Accept-Language"); if (Respond != null)
                {
                    await Respond(c);
                }
                else if (c.Request.Method != "HEAD")
                {
                    await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("body-" + calls));
                }
            };
            var compression = new CompressionMiddleware(upstream, plans);
            _middleware = new(compress ? compression.InvokeAsync : upstream, Store, plans);
        }
        public void Publish(long version) => _publisher.Publish(GatewaySnapshot.Empty with
        {
            Version = version, Routes = [_route],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("test",
                [new("cache", "response-cache", PluginBindingScope.Route, "test", $"{{\"enabled\":true,\"maxBodyBytes\":{_capacity}}}", 1, 1, 0),
                 new("compression", "compression", PluginBindingScope.Route, "test", $"{{\"enabled\":{_compress.ToString().ToLowerInvariant()},\"minResponseSize\":1024}}", 1, 1, 0)])
        });
        public async Task<HttpContext> Send(Action<HttpContext>? configure = null)
        {
            var c = new DefaultHttpContext(); c.Request.Method = "GET"; c.Request.Path = "/test"; c.Request.Scheme = "http"; c.Request.Host = new("example.test");
            c.Response.Body = new MemoryStream();
            c.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(_route, null!, HttpTransformer.Default) });
            configure?.Invoke(c); await _middleware.InvokeAsync(c); return c;
        }
    }
}
