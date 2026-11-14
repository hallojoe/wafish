using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Plugin.Cache;
using Wafish.Plugin.Compression;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static partial class ProtocolPropertyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Protocol properties: WAF corpus reports benign rejections and missed attacks", WafCorpusAsync);
        await run("Protocol properties: seeded WAF decoding and split reads remain bounded", WafFuzzAsync);
        await run("Protocol properties: cache directives preserve freshness, age, and variant isolation", CacheAsync);
        await run("Protocol properties: encoding qualities, duplicates, wildcards, and invalid fields select supported bytes", CompressionAsync);
        await run("Protocol properties: concurrent metric snapshots retain coherent counters and retired identities", MetricsAsync);
    }
    private sealed class Fixture
    {
        private readonly GatewaySnapshotPublisher _publisher = new();
        private readonly RouteConfig _route = new() { RouteId = "property", Match = new() { Path = "/{**path}" } };
        public readonly ReviewReleaseChecks.ManualTime Clock = new();
        public readonly ResponseCacheStore Store;
        public int Calls;
        public Action<HttpContext>? ResponseHeaders;
        private readonly RequestDelegate _pipeline;
        public Fixture(bool compression = false)
        {
            Store = new(Options.Create(new ResponseCacheBudgetOptions { SizeLimitBytes = 1024 * 1024 }), Clock);
            _publisher.Publish(GatewaySnapshot.Empty with
            {
                Version = 1, Routes = [_route],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("property",
                    [new("property", compression ? "compression" : "response-cache", PluginBindingScope.Route, "property",
                        compression ? "{\"enabled\":true,\"minResponseSize\":0}" : "{\"enabled\":true,\"ttlSeconds\":60,\"maxBodyBytes\":4096}", 1, 1, 0)])
            });
            var plans = new GatewayPluginExecutionPlanProvider(_publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            RequestDelegate upstream = async context =>
            {
                Interlocked.Increment(ref Calls);
                context.Response.ContentType = "text/plain";
                context.Response.Headers.CacheControl = "public, max-age=30";
                context.Response.Headers.Date = Clock.GetUtcNow().ToString("R");
                ResponseHeaders?.Invoke(context);
                if (context.Request.Method != "HEAD") await context.Response.Body.WriteAsync(Payload);
            };
            _pipeline = compression ? new CompressionMiddleware(upstream, plans).InvokeAsync : new ResponseCacheMiddleware(upstream, Store, plans).InvokeAsync;
        }
        public static byte[] Payload { get; } = Encoding.UTF8.GetBytes(new string('x', 2048));
        public async Task<HttpContext> Send(Action<HttpContext>? configure = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET"; context.Request.Path = "/property"; context.Request.Scheme = "http"; context.Request.Host = new("property.test");
            context.Response.Body = new MemoryStream();
            context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(_route, null!, HttpTransformer.Default) });
            configure?.Invoke(context); await _pipeline(context); return context;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
