using System.Collections.Immutable;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WafIsh.Services;
using WafIsh.Storage.Entities;
using WafIsh.Infrastructure.Middleware;
using WafIsh.Plugin.RateLimit;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using WafIsh.Dashboard.Infrastructure.Plugin;
using WafIsh.Dashboard.Infrastructure.State;
using WafIsh.Plugin.ProxyLog.Services;
using WafIsh.Plugin.Waf;
using WafIsh.Plugin.Waf.Helpers;

internal static class ReviewSecurityChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Review: invalid CIDR rules fail validation and matching", () =>
        {
            var validator = new PluginConfigurationSchemaValidator();
            var schema = new WafPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
            foreach (var cidr in new[] { "10.0.0.1/-1", "10.0.0.1/33", "::1/-1", "::1/129", "::1/200", "::1/no", "bad/24" })
            {
                Require(!validator.TryValidate(JsonSerializer.Serialize(new { ipWhitelist = new[] { cidr } }), schema, out _, out _), cidr);
                Require(!IpMatcher.IsInCidrRange(cidr, cidr.Contains(':') ? "::1" : "10.0.0.1"), cidr);
            }
            foreach (var (cidr, ip) in new[] { ("0.0.0.0/0", "192.0.2.1"), ("192.0.2.1/32", "192.0.2.1"), ("::/0", "::1"), ("::1/128", "::1") })
            {
                Require(validator.TryValidate(JsonSerializer.Serialize(new { ipBlacklist = new[] { cidr } }), schema, out _, out _), cidr);
                Require(IpMatcher.IsInCidrRange(cidr, ip), cidr);
            }
            Require(!IpMatcher.IsInCidrRange("::/0", "192.0.2.1"), "Mixed families matched.");
            return Task.CompletedTask;
        });
        await run("Review: compressed log capture stops at the decoded byte limit", async () =>
        {
            foreach (var encoding in new[] { "gzip", "br", "deflate" })
            {
                using var compressed = new MemoryStream();
                using (Stream encoder = encoding switch { "gzip" => new GZipStream(compressed, CompressionLevel.SmallestSize, true), "br" => new BrotliStream(compressed, CompressionLevel.SmallestSize, true), _ => new DeflateStream(compressed, CompressionLevel.SmallestSize, true) })
                    encoder.Write(Encoding.UTF8.GetBytes(new string('a', 1024 * 1024)));
                var context = new DefaultHttpContext();
                context.Request.Body = compressed;
                context.Request.ContentLength = compressed.Length;
                context.Request.ContentType = "text/plain";
                context.Request.Headers.ContentEncoding = encoding;
                var request = await ProxyLogBodyReader.ReadRequestBodyAsync(context.Request, 4096);
                Require(request.Length < 4200 && request.Contains("TRUNCATED"), "Request expansion was unbounded.");
                Require(compressed.Position == 0 && compressed.CanRead, "Capture did not preserve the request.");
                var response = await ProxyLogBodyReader.ReadStreamAsync(compressed, 4096, "text/plain", encoding);
                Require(response.Length < 4200 && response.Contains("TRUNCATED"), "Response expansion was unbounded.");
            }
        });
        await run("Review: local limiter admission is bounded under concurrent key churn", async () =>
        {
            using var store = new InMemoryRateLimiterStore();
            await Parallel.ForEachAsync(Enumerable.Range(0, 4000), async (i, ct) =>
            {
                try { using var lease = await store.AcquireAsync(i.ToString(), () => new ConcurrencyLimiter(new() { PermitLimit = 1, QueueLimit = 0 })); }
                catch (InvalidOperationException) { }
                await Task.CompletedTask;
            });
            Require(store.Count <= 2000, "Admission exceeded 2,000 partitions.");
        });
        await run("Review: WAF compiler rejects malformed stored address rules before activation", () =>
        {
            var compiler = new WafConfigurationCompiler();
            Require(compiler.CanCompile("WAF") && !compiler.CanCompile("other"), "Compiler selection failed.");
            foreach (var json in new[] { "null", "{\"ipWhitelist\":null}", "{\"ipBlacklist\":[\"::1/129\"]}" })
            {
                try { compiler.Compile(new("w", "waf", PluginBindingScope.Route, "r", json, 1, 1, 0), new RouteConfig()); throw new Exception("Invalid stored rule accepted."); }
                catch (InvalidOperationException) { }
            }
            compiler.Compile(new("w", "waf", PluginBindingScope.Route, "r", "{\"ipWhitelist\":[\"192.0.2.0/24\",\"192.0.*\"]}", 1, 1, 0), new RouteConfig());
            return Task.CompletedTask;
        });
        await run("Review: limiter leases survive cleanup, queue cancellation, and shutdown", async () =>
        {
            using var store = new InMemoryRateLimiterStore();
            var factories = 0;
            RateLimiter Factory() { Interlocked.Increment(ref factories); return new ConcurrencyLimiter(new() { PermitLimit = 1, QueueLimit = 1 }); }
            using var first = await store.AcquireAsync("active", Factory);
            Require(first!.IsAcquired, "Initial permit failed.");
            store.Cleanup(TimeSpan.FromTicks(-1), 0);
            Require(store.Count == 1, "Cleanup removed an active lease.");
            using var cancel = new CancellationTokenSource();
            var waiting = store.AcquireAsync("active", Factory, cancel.Token).AsTask();
            cancel.Cancel();
            try { await waiting; throw new Exception("Queue cancellation ignored."); } catch (OperationCanceledException) { }
            first.Dispose(); first.Dispose();
            using var second = await store.AcquireAsync("active", Factory);
            Require(second!.IsAcquired && factories == 1, "Existing limiter was replaced.");
            _ = second.MetadataNames.ToArray(); second.TryGetMetadata("missing", out _);
            store.Dispose();
            second.Dispose();
            try { await store.AcquireAsync("after", Factory); throw new Exception("Disposed store accepted a key."); } catch (ObjectDisposedException) { }
            using var idleStore = new InMemoryRateLimiterStore();
            using (var idleLease = await idleStore.AcquireAsync("idle", Factory)) { }
            idleStore.Cleanup(TimeSpan.FromTicks(-1), 0);
            Require(idleStore.Count == 0, "Idle quota was retained.");
        });
        await run("Review: route partitions stay stable and overflow returns 429", async () =>
        {
            using var store = new InMemoryRateLimiterStore();
            var publisher = new GatewaySnapshotPublisher();
            var route = new RouteConfig { RouteId = "stable-route" };
            publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                    [new("limit", "rate-limit", PluginBindingScope.Route, route.RouteId, "{\"enabled\":true,\"partitionKey\":\"Route\",\"permitLimit\":2,\"window\":\"1h\"}", 1, 1, 0)]) });
            var plans = new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            var calls = 0;
            var middleware = new RateLimitMiddleware(c => { calls++; return Task.CompletedTask; }, NullLogger<RateLimitMiddleware>.Instance,
                Options.Create(new GatewayMiddlewareOptions()), null!, store, plans);
            for (var i = 0; i < 100; i++)
            {
                var c = new DefaultHttpContext(); c.Request.Path = "/items/" + i; c.Response.Body = new MemoryStream();
                c.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
                await middleware.InvokeAsync(c);
                Require(c.Response.StatusCode == (i < 2 ? 200 : 429), "Variable paths escaped the route quota.");
            }
            Require(store.Count == 1 && calls == 2, "Variable paths created partitions.");
            for (var i = 1; i < 2000; i++) { using var lease = await store.AcquireAsync("fill-" + i, () => new ConcurrencyLimiter(new() { PermitLimit = 1, QueueLimit = 0 })); }
            Require(await store.AcquireAsync("overflow", () => throw new Exception("Overflow factory ran")) == null, "Overflow admitted a key.");
            var other = route with { RouteId = "new-route" };
            publisher.Publish(GatewaySnapshot.Empty with { Version = 2, Routes = [other],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(other.RouteId,
                    [new("limit", "rate-limit", PluginBindingScope.Route, other.RouteId, "{\"enabled\":true}", 1, 1, 0)]) });
            var overflow = new DefaultHttpContext(); overflow.Request.Path = "/overflow"; overflow.Response.Body = new MemoryStream();
            overflow.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(other, null!, HttpTransformer.Default) });
            await middleware.InvokeAsync(overflow);
            Require(overflow.Response.StatusCode == 429 && overflow.Response.Headers.RetryAfter.Count == 1 && calls == 2,
                "Capacity overflow forwarded a request or lost retry guidance.");
        });
        await run("Review: log decoding handles boundaries, cancellation, corruption, and stream ownership", async () =>
        {
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("ééé"));
            Require(await ProxyLogBodyReader.ReadStreamAsync(bytes, 5, "text/plain", null) == "éé\n... [TRUNCATED - decoded body too large]", "UTF-8 split was retained.");
            Require(await ProxyLogBodyReader.ReadStreamAsync(bytes, 6, "text/plain", "identity") == "ééé", "Exact limit changed content.");
            Require(await ProxyLogBodyReader.ReadStreamAsync(bytes, 0) == "", "Disabled capture returned content.");
            using var corrupt = new MemoryStream("invalid compressed data"u8.ToArray());
            Require((await ProxyLogBodyReader.ReadStreamAsync(corrupt, 20, "text/plain", "gzip")).Contains("unavailable"), "Corruption was hidden.");
            try { await ProxyLogBodyReader.ReadStreamAsync(bytes, 10, null, null, new CancellationToken(true)); throw new Exception("Decode cancellation ignored."); }
            catch (OperationCanceledException) { }
            Require(bytes.CanRead && corrupt.CanRead, "Decoder disposed caller streams.");
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
