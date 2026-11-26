using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugin.Compression;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class CompressionPolicyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Compression policy: allowed encodings preserve qualities, identity fallback, levels, and Vary", async () =>
        {
            var validator = new PluginConfigurationSchemaValidator();
            var schema = new CompressionPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
            foreach (var json in new[] { "{\"enabled\":true}", "{\"enabled\":true,\"allowedEncodings\":[]}",
                "{\"enabled\":true,\"allowedEncodings\":[\"gzip\"],\"gzipCompressionLevel\":\"Fastest\"}" })
                Check(validator.TryValidate(json, schema, out _, out _), "Valid compression policy was rejected.");
            foreach (var json in new[] { "{\"enabled\":true,\"allowedEncodings\":[\"deflate\"]}",
                "{\"enabled\":true,\"allowedEncodings\":[\"br\",\"br\"]}", "{\"enabled\":true,\"allowedEncodings\":null}",
                "{\"enabled\":true,\"gzipCompressionLevel\":\"invalid\"}", "{\"enabled\":true,\"brotliCompressionLevel\":\"invalid\"}" })
                Check(!validator.TryValidate(json, schema, out _, out _), "Invalid encoding policy was accepted.");
            foreach (var (allowed, accept, expected) in new (string[]?, string, string)[] {
                (null, "br,gzip", "br"), (["gzip"], "br,gzip", "gzip"), (["br"], "gzip", "identity"),
                ([], "*", "identity"), (["gzip"], "*;q=0.5", "gzip"), (["gzip"], "gzip;q=0,*;q=1", "identity"),
                (["br"], "br;q=0.5,br;q=0,gzip;q=1", "identity"), (["br","gzip"], "br;q=0.5,gzip;q=0.8", "gzip"),
                (["br","gzip"], "br;q=2,gzip;q=1", "identity"), (["br"], "identity;q=1,br;q=0", "identity") })
            {
                var settings = new Dictionary<string, object?> { ["enabled"] = true, ["minResponseSize"] = 0 };
                if (allowed is not null) settings["allowedEncodings"] = allowed;
                var (encoding, body) = await Send(settings, accept);
                Check(encoding == expected, "Disabled algorithm or quality selection changed: " + accept);
                Check(Decode(encoding, body).SequenceEqual(Payload), "Encoding policy changed response bytes.");
            }
            // Raw snapshots can bypass schema validation. Invalid null lists must fail closed.
            var nullPolicy = await Send(new { enabled = true, minResponseSize = 0, allowedEncodings = (string[]?)null }, "br,gzip");
            Check(nullPolicy.Encoding == "identity" && nullPolicy.Body.SequenceEqual(Payload), "Null policy enabled an algorithm.");
            foreach (var encoding in new[] { "br", "gzip" })
            foreach (var level in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.NoCompression })
            {
                var settings = new Dictionary<string, object?> { ["enabled"] = true, ["minResponseSize"] = 0,
                    ["compressionLevel"] = "NoCompression", [encoding == "br" ? "brotliCompressionLevel" : "gzipCompressionLevel"] = level.ToString() };
                var (_, body) = await Send(settings, encoding);
                using var expected = new MemoryStream();
                await using (Stream codec = encoding == "br" ? new BrotliStream(expected, level, true) : new GZipStream(expected, level, true))
                    await codec.WriteAsync(Payload);
                Check(body.SequenceEqual(expected.ToArray()), "Per-algorithm compression level did not reach the codec.");
                settings.Remove(encoding == "br" ? "brotliCompressionLevel" : "gzipCompressionLevel");
                settings["compressionLevel"] = level.ToString();
                Check((await Send(settings, encoding)).Body.SequenceEqual(body), "Shared-level compatibility fallback changed.");
            }
        });
    }
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("bounded compression policy payload; ", 256)));
    private static async Task<(string Encoding, byte[] Body)> Send(object settings, string accept)
    {
        var route = new RouteConfig { RouteId = "compression-policy", Match = new() { Path = "/policy" } };
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with
        {
            Version = 1, Routes = [route], RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                [new("policy", "compression", PluginBindingScope.Route, route.RouteId, JsonSerializer.Serialize(settings), 1, 1, 0)])
        });
        using var output = new MemoryStream();
        var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Headers.AcceptEncoding = accept;
        context.Response.Body = output; context.Response.ContentType = "application/json"; context.Response.ContentLength = Payload.Length;
        context.Response.Headers.Vary = "Origin";
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        var middleware = new CompressionMiddleware(c => c.Response.Body.WriteAsync(Payload).AsTask(),
            new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
        await middleware.InvokeAsync(context);
        Check(context.Response.Headers.Vary.ToString().Contains("Origin") && context.Response.Headers.Vary.ToString().Contains("Accept-Encoding"), "Encoding restrictions lost Vary.");
        return (context.Response.Headers.ContentEncoding.Count == 0 ? "identity" : context.Response.Headers.ContentEncoding.ToString(), output.ToArray());
    }
    private static byte[] Decode(string encoding, byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using Stream codec = encoding switch { "br" => new BrotliStream(input, CompressionMode.Decompress), "gzip" => new GZipStream(input, CompressionMode.Decompress), _ => input };
        using var body = new MemoryStream(); codec.CopyTo(body); return body.ToArray();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
