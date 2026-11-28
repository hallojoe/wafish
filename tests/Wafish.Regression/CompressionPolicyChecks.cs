using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Models;
using Wafish.Plugin.Compression;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class CompressionPolicyChecks
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 500).Select(i => $"row {i % 31}: repeated text and identifiers {i}")));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static CompressionExecutionConfig Policy() => new() { MinResponseSize = 0 };
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Compression policy: validates bounded encoding lists and independent levels", () =>
        {
            var compiler = new CompressionPolicyCompiler();
            CompiledRoutePlugin Compile(CompressionExecutionConfig config, int schema = 1) => compiler.Compile(
                new("id", "compression", PluginBindingScope.Route, "route", JsonSerializer.Serialize(config, Json), schema, 1, 0), new() { RouteId = "route" });
            Check(compiler.CanCompile("COMPRESSION") && !compiler.CanCompile("rate-limit"), "Compiler selection changed.");
            Compile(Policy()); Compile(Policy() with { AllowedEncodings = [] });
            foreach (var level in new[] { "Optimal", "Fastest", "NoCompression" }) Compile(Policy() with { BrotliCompressionLevel = level, GzipCompressionLevel = level });
            foreach (var invalid in new[]
            {
                Policy() with { AllowedEncodings = ["deflate"] }, Policy() with { AllowedEncodings = ["BR"] },
                Policy() with { AllowedEncodings = ["br", "br"] }, Policy() with { AllowedEncodings = ["br", "gzip", "br"] },
                Policy() with { AllowedEncodings = [null!] }, Policy() with { BrotliCompressionLevel = "fastest" },
                Policy() with { GzipCompressionLevel = "" }, Policy() with { GzipCompressionLevel = "unknown" }
            }) Reject(() => Compile(invalid));
            Reject(() => Compile(Policy() with { AllowedEncodings = ["gzip"] }, 2));
            Reject(() => compiler.Compile(new("id", "compression", PluginBindingScope.Route, "route", "null", 1, 1, 0), new() { RouteId = "route" }));
            var original = Policy() with { AllowedEncodings = ["br"] };
            var copied = CompressionEncodingPolicy.Compile(original); original.AllowedEncodings![0] = "gzip";
            Check(copied.AllowedEncodings![0] == "br", "Compiled policy retained a mutable caller array.");
            return Task.CompletedTask;
        });
        await run("Compression policy: disabled encodings never win quality or wildcard negotiation", async () =>
        {
            var cases = new (string Header, string? Both, string? Brotli, string? Gzip)[]
            {
                ("br, gzip", "br", "br", "gzip"), ("gzip;q=1, br;q=0.5", "gzip", "br", "gzip"),
                ("br;q=1, gzip;q=0.5", "br", "br", "gzip"), ("*", "br", "br", "gzip"),
                ("br;q=0, *;q=1", "gzip", null, "gzip"), ("gzip;q=0, *;q=1", "br", "br", null),
                ("br", "br", "br", null), ("gzip", "gzip", null, "gzip"), ("identity", null, null, null),
                ("br;q=0, gzip;q=0", null, null, null), ("br;q=bad, gzip", null, null, null),
                ("br;q=0.1234, gzip", null, null, null), ("br;q=0, br;q=1, gzip;q=0.5", "gzip", null, "gzip"),
                ("gzip;q=0, gzip;q=1, *;q=0.5", "br", "br", null)
            };
            foreach (var allowed in new string[]?[] { null, ["br", "gzip"], ["br"], ["gzip"], [] })
            foreach (var item in cases)
            {
                var expected = allowed?.Length switch { 0 => null, 1 => allowed[0] == "br" ? item.Brotli : item.Gzip, _ => item.Both };
                var response = await Send(Policy() with { AllowedEncodings = allowed }, item.Header);
                Check(response.Encoding == expected && response.Vary == "Origin,Accept-Encoding", "Encoding restriction broke negotiation or Vary: " + item.Header);
                using var bytes = new MemoryStream(response.Bytes);
                using Stream decoder = expected switch { "br" => new BrotliStream(bytes, CompressionMode.Decompress), "gzip" => new GZipStream(bytes, CompressionMode.Decompress), _ => bytes };
                using var decoded = new MemoryStream(); await decoder.CopyToAsync(decoded);
                Check(decoded.ToArray().SequenceEqual(Payload), "Policy changed body bytes.");
            }
        });
        await run("Compression policy: per-codec levels override only their encoding and preserve shared fallback", async () =>
        {
            foreach (var encoding in new[] { "br", "gzip" })
            foreach (var shared in new[] { "Optimal", "Fastest", "NoCompression" })
            foreach (var selected in new string?[] { null, "Optimal", "Fastest", "NoCompression" })
            {
                var policy = Policy() with { CompressionLevel = shared,
                    BrotliCompressionLevel = encoding == "br" ? selected : "Fastest",
                    GzipCompressionLevel = encoding == "gzip" ? selected : "Fastest" };
                var response = await Send(policy, encoding);
                using var expected = new MemoryStream();
                var level = Enum.Parse<CompressionLevel>(selected ?? shared);
                await using (Stream codec = encoding == "br" ? new BrotliStream(expected, level, true) : new GZipStream(expected, level, true))
                    await codec.WriteAsync(Payload);
                Check(response.Bytes.SequenceEqual(expected.ToArray()), "Selected encoding did not use its configured level: " + encoding + "/" + shared + "/" + selected);
            }
        });
        await run("Compression policy: invalid active and disabled candidates leave persisted and active state unchanged", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start();
            var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await routes.TryAddRouteConfig(new() { RouteId = "policy", Match = new() { Path = "/policy" } })).Success, "Route creation failed.");
            var snapshots = fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            var repository = fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var before = snapshots.Current;
            foreach (var enabled in new[] { true, false })
            {
                try
                {
                    await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(new()
                    { PluginId = "compression", PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "policy", Enabled = enabled,
                      ConfigJson = "{\"enabled\":true,\"allowedEncodings\":[\"gzip\"],\"brotliCompressionLevel\":\"invalid\"}" });
                    throw new Exception("Invalid policy was persisted.");
                }
                catch (ArgumentException) { }
                Check(!(await repository.GetBindingsAsync()).Any() && ReferenceEquals(before, snapshots.Current), "Rejected policy changed persisted or active configuration.");
            }
            await fixture.Bind("policy", "compression", JsonSerializer.Serialize(Policy() with { AllowedEncodings = ["gzip"], GzipCompressionLevel = "Fastest" }, Json));
            Check((await repository.GetBindingsAsync()).Single().PluginId == "compression", "Valid policy did not persist.");
            Check(fixture.App.Services.GetRequiredService<CompressionMetricStore>().GetSnapshots().Count == 0, "Configuration preview executed compression.");
        });
    }
    private static async Task<(byte[] Bytes, string? Encoding, string Vary)> Send(CompressionExecutionConfig config, string accept)
    {
        var route = new RouteConfig { RouteId = "route" };
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route], RoutePlugins =
            ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("route",
                [new("binding", "compression", PluginBindingScope.Route, "route", JsonSerializer.Serialize(config, Json), 1, 1, 0)]) });
        var context = new DefaultHttpContext(); context.Request.Headers.AcceptEncoding = accept;
        context.Response.ContentType = "text/plain"; context.Response.Headers.Vary = "Origin";
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        using var output = new MemoryStream(); context.Response.Body = output;
        var middleware = new CompressionMiddleware(c => c.Response.Body.WriteAsync(Payload).AsTask(), new(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
        await middleware.InvokeAsync(context);
        return (output.ToArray(), context.Response.Headers.ContentEncoding.FirstOrDefault(), string.Join(',', context.Response.Headers.Vary.ToString().Split(',', StringSplitOptions.TrimEntries)));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); throw new Exception("Invalid policy accepted."); } catch (ArgumentException) { } }
}
