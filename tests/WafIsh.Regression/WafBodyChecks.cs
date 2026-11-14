using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugin.Waf;
using Wafish.Services;
using Wafish.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class WafBodyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("WAF: rejection logs contain rule, route, and trace without request secrets", async () =>
        {
            var logger = new CaptureLogger();
            var secret = "seeded-secret-17393";
            var result = await Invoke(Encoding.UTF8.GetBytes("password=" + secret + "<script>alert(1)</script>"), 1024,
                path: "/" + secret, logger: logger, query: "?token=" + secret);
            Check(result.Status == 403, "Attack was not blocked.");
            var logs = string.Join("\n", logger.Messages);
            Check(!logs.Contains(secret) && !logs.Contains("<script>"), "Request content leaked into logs.");
            Check(logs.Contains("XssBlocked") && logs.Contains("waf-test") && logs.Contains("review-trace"), "Rejection context missing.");
        });
        await run("WAF: unknown-length short reads are fully inspected and preserved", async () =>
        {
            var valid = Encoding.UTF8.GetBytes("name=hello&text=caf%C3%A9");
            var pass = await Invoke(valid, 1024);
            Check(pass.Status == 200 && pass.Forwarded!.SequenceEqual(valid), "Valid body changed during inspection.");
            var attack = Encoding.UTF8.GetBytes(new string('a', 200) + "<script>alert(1)</script>");
            var blocked = await Invoke(attack, 1024);
            Check(blocked.Status == 403 && blocked.Forwarded == null, "Short reads bypassed inspection.");
            var shadow = await Invoke(attack, 1024, path: "/apigateway-shadow");
            Check(shadow.Status == 403, "Dashboard prefix bypassed a configured WAF binding.");
        });
        await run("WAF: body and inspection limits count bytes without Content-Length", async () =>
        {
            Check((await Invoke(new byte[17], 16, inspect: false)).Status == 413, "Chunked size limit was bypassed.");
            Check((await Invoke(Encoding.UTF8.GetBytes("ééééé"), 9)).Status == 413, "Limit counted characters instead of bytes.");
            Check((await Invoke(new byte[WafMiddleware.MaxInspectionBodyBytes + 1], 1024 * 1024)).Status == 413, "Inspection overflow was accepted.");
            Check((await Invoke(Encoding.UTF8.GetBytes("hello"), 5)).Forwarded?.Length == 5, "Exact body limit was rejected.");
        });
        await run("WAF: decoding, read failures, and cancellation fail closed", async () =>
        {
            Check((await Invoke([0xff, 0xfe], 100)).Status == 400, "Invalid UTF-8 was accepted.");
            Check((await Invoke([1, 2, 3], 100, failRead: true)).Status == 503, "Read failure reached upstream.");
            try { await Invoke([1, 2, 3], 100, cancel: true); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
        });
    }

    private static async Task<(int Status, byte[]? Forwarded)> Invoke(byte[] body, long limit,
        bool inspect = true, bool failRead = false, bool cancel = false, string path = "/waf-test", ILogger<WafMiddleware>? logger = null, string? query = null)
    {
        var route = new RouteConfig { RouteId = "waf-test", Match = new() { Path = path } };
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with
        {
            Version = 1, Routes = [route],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("waf-test",
                [new("waf-binding", "waf", PluginBindingScope.Route, "waf-test",
                    JsonSerializer.Serialize(new { enabled = true, enableIpCheck = false, enableSqlInjectionDetection = inspect,
                        enableXssDetection = inspect, enableRequestSizeValidation = true, maxRequestBodySize = limit }), 1, 1, 0)])
        });
        var plans = new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
        byte[]? forwarded = null;
        var middleware = new WafMiddleware(async ctx =>
        {
            using var output = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(output, ctx.RequestAborted);
            forwarded = output.ToArray();
        }, logger ?? NullLogger<WafMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, plans);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.TraceIdentifier = "review-trace";
        context.Request.QueryString = new QueryString(query);
        context.Request.Method = "POST";
        context.Request.ContentType = "text/plain; charset=utf-8";
        context.Request.Body = new ShortStream(body, failRead);
        context.Response.Body = new MemoryStream();
        if (cancel)
        {
            context.RequestAborted = new CancellationToken(canceled: true);
        }

        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        try
        {
            await middleware.InvokeAsync(context);
            return (context.Response.StatusCode, forwarded);
        }
        finally { await context.Request.Body.DisposeAsync(); await context.Response.Body.DisposeAsync(); }
    }

    private sealed class CaptureLogger : ILogger<WafMiddleware>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class ShortStream(byte[] bytes, bool fail) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fail)
            {
                throw new IOException("Injected read error");
            }

            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    private static void Check(bool value, string message) { if (!value)
        {
            throw new Exception(message);
        }
    }
}
