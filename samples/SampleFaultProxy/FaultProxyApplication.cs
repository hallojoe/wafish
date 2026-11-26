using System.Net;
using System.Security.Cryptography;
using System.Text;
using Yarp.ReverseProxy.Configuration;

namespace SampleFaultProxy;

public static class FaultProxyApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var port = builder.Configuration.GetValue<int>("FAULT_HTTP_PORT");
        var key = builder.Configuration["Fault:ControlKey"] ?? "";
        if (port is < 0 or > 65535 || key.Length < 32) throw new ArgumentException("A valid port and private control key are required.");
        if (!Uri.TryCreate(builder.Configuration["Fault:Target"], UriKind.Absolute, out var target) ||
            target.Scheme != "http" || !(target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(target.Host, out var address) && IPAddress.IsLoopback(address)) ||
            target.Port == 0 || target.AbsolutePath != "/" || target.Query.Length != 0 || target.Fragment.Length != 0 || target.UserInfo.Length != 0)
            throw new ArgumentException("Fault target must be one explicit loopback HTTP origin.");
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(server => { server.Limits.MaxRequestBodySize = 4096; server.Limits.MaxConcurrentConnections = 128; });
        builder.Services.AddReverseProxy().LoadFromMemory(
            [new RouteConfig { RouteId = "sample", ClusterId = "sample", Match = new() { Path = "/api/local-service/{**catch-all}" } }],
            [new ClusterConfig { ClusterId = "sample", Destinations = new Dictionary<string, DestinationConfig> { ["backend"] = new() { Address = target.ToString() } } }]);
        var app = builder.Build();
        var state = new FaultState();
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/__fault"))
            {
                var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Fault-Key"].ToString()));
                if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) { context.Response.StatusCode = 401; return; }
                context.Response.Headers.CacheControl = "no-store";
            }
            await next(context);
        });
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/__fault", () => Results.Ok(state.Snapshot()));
        app.MapPost("/__fault", (FaultRequest request) => state.Configure(request) ? Results.Ok(state.Snapshot()) : Results.BadRequest());
        app.MapReverseProxy(proxy => proxy.Use(async (context, next) =>
        {
            context.Request.Headers.Remove("X-Fault-Key");
            var fault = state.Take();
            try
            {
                if (fault.Mode == "reset") { context.Abort(); return; }
                if (fault.Mode == "delay") await Task.Delay(fault.DelayMilliseconds, context.RequestAborted);
                await next(context);
            }
            finally { state.Finish(); }
        }));
        return app;
    }
}

public sealed record FaultRequest(string Mode, int Count = 0, int DelayMilliseconds = 0);

public sealed class FaultState
{
    private readonly object _gate = new();
    private FaultRequest _fault = new("none");
    private long _attempts, _resets, _delays;
    private int _active;
    public bool Configure(FaultRequest request)
    {
        if (request.Mode is not ("none" or "reset" or "delay") || request.Count is < 0 or > 100 ||
            request.DelayMilliseconds is < 0 or > 5000 || request.Mode != "none" && request.Count == 0 ||
            request.Mode == "delay" && request.DelayMilliseconds == 0) return false;
        lock (_gate) { _fault = request.Mode == "none" ? new("none") : request; }
        return true;
    }
    public FaultRequest Take()
    {
        lock (_gate)
        {
            _attempts++; _active++;
            var result = _fault;
            if (_fault.Count > 0) _fault = _fault.Count == 1 ? new("none") : _fault with { Count = _fault.Count - 1 };
            if (result.Mode == "reset") _resets++;
            if (result.Mode == "delay") _delays++;
            return result;
        }
    }
    public void Finish() { lock (_gate) { _active--; } }
    public object Snapshot() { lock (_gate) { return new { mode = _fault.Mode, remaining = _fault.Count, attempts = _attempts, resets = _resets, delays = _delays, active = _active }; } }
}
