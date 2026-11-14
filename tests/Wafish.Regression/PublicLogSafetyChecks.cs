using Wafish.Dashboard.Infrastructure.Plugin;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Infrastructure.Performance;
using Wafish.Plugin.ProxyLog;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Plugin.ProxyLog.Services;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class PublicLogSafetyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Public logs: route rules redact before persistence and realtime delivery", async () =>
        {
            const string body = """{"customer":{"id":"private-id","name":"public-name"},"items":[{"value":"private-item"}],"a/b":{"~key":"private-escaped"},"password":"private-password","number":9007199254740993,"empty":null}""";
            var entries = await Capture(body, ["/customer/id", "/items/*/value", "/a~1b/~0key"]);
            Require(entries.Count == 2, "Missing capture entries.");
            var encoded = JsonSerializer.Serialize(entries);
            Require(!encoded.Contains("private-") && encoded.Contains("public-name") && encoded.Contains("9007199254740993"), "Redaction lost public data or leaked secrets.");
            var isolated = JsonSerializer.Serialize(await Capture(body, []));
            Require(isolated.Contains("private-id") && !isolated.Contains("private-password"), "Route rules leaked into another binding or disabled defaults.");
        });
        await run("Public logs: incomplete JSON and invalid rules fail closed", async () =>
        {
            foreach (var body in new[] { "{\"customer\":\"private-value", "private-value", new string('[', 70) + "\"private-value\"" + new string(']', 70) })
                Require(!JsonSerializer.Serialize(await Capture(body, ["/customer"])).Contains("private-"), "Unsafe body escaped redaction.");
            foreach (var paths in new[] { new[] { "invalid" }, new[] { "/a~2" }, Enumerable.Repeat("/id", 65).ToArray(), new[] { "/" + new string('x', 256) }, new[] { string.Concat(Enumerable.Repeat("/x", 17)) } })
                Require(!JsonSerializer.Serialize(await Capture("{\"id\":\"private-value\"}", paths)).Contains("private-"), "Invalid rules escaped redaction.");
            Require(!JsonSerializer.Serialize(await Capture("{\"password\":\"private-value", [])).Contains("private-"), "Default sanitizer published incomplete JSON.");
            await Capture("", ["/id"]);
        });
        await run("Public logs: redaction schema bounds lists and pointer syntax", () =>
        {
            var validator = new PluginConfigurationSchemaValidator();
            var schema = new ProxyLogPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
            foreach (var path in new[] { "/items/*/value", "/a~1b/~0key", "/" })
                Require(validator.TryValidate(JsonSerializer.Serialize(new { redactJsonPaths = new[] { path } }), schema, out _, out _), "Valid pointer rejected.");
            foreach (var path in new[] { "missing-slash", "/a~2", new string('/', 17) })
                Require(!validator.TryValidate(JsonSerializer.Serialize(new { redactJsonPaths = new[] { path } }), schema, out _, out _), "Invalid pointer accepted.");
            Require(!validator.TryValidate(JsonSerializer.Serialize(new { redactHeaders = Enumerable.Repeat("x", 65) }), schema, out _, out _), "Unbounded headers accepted.");
            return Task.CompletedTask;
        });
    }

    private static async Task<List<LogEntry>> Capture(string body, string[] paths)
    {
        var route = new RouteConfig { RouteId = "redaction", Match = new() { Path = "/" } };
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with
        {
            Version = 1, Routes = [route],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                [new("logs", "proxy-log", PluginBindingScope.Route, route.RouteId,
                    JsonSerializer.Serialize(new { requestBodyCaptureEnabled = true, responseBodyCaptureEnabled = true,
                        redactHeaders = new[] { "X-Customer" }, redactJsonPaths = paths }), 1, 1, 0)])
        });
        var store = new ProxyLogStore();
        var realtime = new List<LogEntry>();
        var extension = typeof(ProxyLogStore).Assembly.GetType("Wafish.Plugin.ProxyLog.Services.ProxyLogStoreExtensions")!;
        using var subscription = (IDisposable)extension.GetMethod("OnNewEntry")!.Invoke(null, [store, (Action<LogEntry>)realtime.Add])!;
        var options = Options.Create(new ProxyLogPluginOptions { LogHeaderBlacklist = ["X-Custom"], LogJsonFieldSanitizeList = ["custom"] });
        var middleware = new YarpRequestCaptureMiddleware(async c =>
        {
            c.Response.ContentType = "application/json";
            c.Response.Headers["X-Customer"] = "private-response-header";
            c.Response.Headers.SetCookie = "private-cookie";
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
        }, new Filter(), new ProxyLogCapture(store), new LogSanitizer(options), new RecyclableMemoryStreamManager(),
            new LockFreeStatistics(), new ProxyLogRuntimeSettings(options),
            new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
        var context = new DefaultHttpContext();
        var model = new RouteModel(route, null!, HttpTransformer.Default);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(model), "redaction"));
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = model });
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Authorization = "private-authorization";
        context.Request.Headers["X-Customer"] = "private-request-header";
        context.Request.Headers["X-Custom"] = "private-custom-header";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var output = new MemoryStream();
        context.Request.Body = input; context.Request.ContentLength = input.Length; context.Response.Body = output;
        await middleware.InvokeAsync(context);
        Require(Encoding.UTF8.GetString(output.ToArray()) == body, "Logging changed the wire response.");
        var persisted = new List<LogEntry>();
        while (store.PersistenceReader.TryRead(out var entry)) persisted.Add(entry);
        Require(JsonSerializer.Serialize(persisted) == JsonSerializer.Serialize(realtime), "Realtime and persistence content differ.");
        return persisted;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Filter : ILogFilter
    {
        public bool IsSkippedRequest(HttpContext context) => false;
        public bool ShouldLog(HttpContext context, string? routeId) => true;
    }
}
