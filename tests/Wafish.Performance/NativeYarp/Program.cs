using Wafish.Performance;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
using var transport = BenchmarkTransport.Read(args);
var listener = transport.Address(BenchmarkOptions.Read(args, "--listen-url", $"{transport.Scheme}://127.0.0.1:5301"), "--listen-url");
var backends = BenchmarkDestinations.Read(args, transport);
builder.WebHost.ConfigureBenchmarkListener(listener, transport);

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
        HttpRequest = new ForwarderRequestConfig { Version = transport.Version, VersionPolicy = HttpVersionPolicy.RequestVersionExact },
        LoadBalancingPolicy = backends.LoadBalancingPolicy,
            Destinations = backends.Addresses.Select((address, index) => KeyValuePair.Create(BenchmarkDestinations.Id(index),
                new DestinationConfig { Address = address.ToString() })).ToDictionary()
    }]);

builder.Services.AddSingleton<IForwarderHttpClientFactory>(new BenchmarkForwarderHttpClientFactory(transport));
var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/benchmark/manifest", (IProxyConfigProvider provider) => Results.Json(BenchmarkManifest.Create(new
{
    mode = "native", proxyVersion = typeof(IProxyConfigProvider).Assembly.GetName().Version?.ToString(),
    routes = provider.GetConfig().Routes, clusters = provider.GetConfig().Clusters, listener = listener.ToString(),
    clientTransport = transport.Description, upstreamTransport = transport.Description
})));
app.MapReverseProxy();
app.Run();
