using Wafish.Performance;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var listener = BenchmarkOptions.LoopbackAddress(BenchmarkOptions.Read(args, "--listen-url", "http://127.0.0.1:5301"), "--listen-url");
var backend = BenchmarkOptions.LoopbackAddress(BenchmarkOptions.Read(args, "--backend-url", "http://127.0.0.1:5300"), "--backend-url");
builder.WebHost.UseUrls(listener.ToString());

builder.Services.AddReverseProxy().LoadFromMemory(
    [new Yarp.ReverseProxy.Configuration.RouteConfig
    {
        RouteId = "perf",
        ClusterId = "backend",
        Match = new Yarp.ReverseProxy.Configuration.RouteMatch { Path = "/api/perf/{**catch-all}" }
    }],
    [new Yarp.ReverseProxy.Configuration.ClusterConfig
    {
        ClusterId = "backend",
        HttpRequest = new ForwarderRequestConfig { Version = System.Net.HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact },
        Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>
        {
            ["backend"] = new() { Address = backend.ToString() }
        }
    }]);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/benchmark/manifest", (IProxyConfigProvider provider) => Results.Json(BenchmarkManifest.Create(new
{
    mode = "native", proxyVersion = typeof(IProxyConfigProvider).Assembly.GetName().Version?.ToString(),
    routes = provider.GetConfig().Routes, clusters = provider.GetConfig().Clusters, listener = listener.ToString(),
    clientTransport = "HTTP/1.1 cleartext", upstreamTransport = "HTTP/1.1 exact cleartext"
})));
app.MapReverseProxy();
app.Run();
