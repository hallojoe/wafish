using Microsoft.Extensions.Configuration;
using Aspire.Hosting.ApplicationModel;
using Wafish.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var topology = TopologyState.Read(builder.Configuration, builder.AppHostDirectory);
using var topologyLease = topology.Acquire();
if (builder.Configuration.GetValue<bool>("EnableSonarQube"))
{
    builder.AddSonarQube(topology);
}

var password = builder.AddParameter(topology.Parameter("control-plane-password"),
    new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true);
var dataDirectory = topology.DataDirectory;

// Direct loopback HTTP avoids HTTP/1 and h2c negotiation on one listener.
// Production administration still requires HTTPS outside direct loopback access.
IResourceBuilder<ProjectResource> AddGateway(string name, bool registration) => builder.AddProject<Projects.SampleGateway>(name, project =>
    { project.ExcludeLaunchProfile = true; project.ExcludeKestrelEndpoints = true; })
    .WithHttpEndpoint(name: "http", env: "GATEWAY_HTTP_PORT", isProxied: false)
    .WithHttpEndpoint(name: "grpc", env: "GATEWAY_GRPC_PORT", isProxied: false)
    .WithEndpoint("grpc", endpoint => { endpoint.Transport = "http2"; endpoint.ExcludeReferenceEndpoint = true; })
    .WithUrlForEndpoint("http", url => { url.Url = "/console"; url.DisplayText = "Gateway console"; })
    .WithUrlForEndpoint("grpc", url =>
    {
        url.DisplayText = "gRPC (HTTP/2 clients only)";
        url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
    })
    .WithEnvironment("Sample__Orchestrated", "true")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_AUTO_BROWSER", "0")
    .WithEnvironment("Gateway__Registration__Enabled", registration ? "true" : "false")
    .WithEnvironment("Gateway__Security__ControlPlane__AuthMode", "BasicAuth")
    .WithEnvironment("Gateway__Security__ControlPlane__Username", "admin")
    .WithEnvironment("Gateway__Security__ControlPlane__Password", password)
    .WithEnvironment("Gateway__Dashboard__Auth__TwoFactorStatePath", Path.Combine(dataDirectory, name + "-twofactor.json"))
    .WithEnvironment("Gateway__Storage__Sqlite__ConnectionString", $"Data Source={Path.Combine(dataDirectory, name + ".db")}")
    .WithHttpHealthCheck("/ready", endpointName: "http");

var gateway = AddGateway("gateway", true);
var backends = new List<IResourceBuilder<ProjectResource>>();

foreach (var instance in new[] { "backend-one", "backend-two" })
{
    var backend = builder.AddProject<Projects.SampleLocalService>(instance, project =>
        { project.ExcludeLaunchProfile = true; project.ExcludeKestrelEndpoints = true; })
        .WithHttpEndpoint(name: "http", env: "BACKEND_HTTP_PORT", isProxied: false)
        .WithUrlForEndpoint("http", url => { url.Url = "/api/local-service/instance"; url.DisplayText = "Backend instance"; })
        .WithEnvironment("Sample__Orchestrated", "true")
        .WithEnvironment("Gateway__Registration__Enabled", "true")
        .WithEnvironment("Gateway__Registration__GatewayUrl", gateway.GetEndpoint("http"))
        .WithEnvironment("Gateway__Registration__GrpcUrl", gateway.GetEndpoint("grpc"))
        .WithEnvironment("Gateway__Registration__RouteName", "sample-service")
        .WithEnvironment("Gateway__Registration__ClusterName", "sample-service")
        .WithEnvironment("Gateway__Registration__InstanceId", instance)
        .WithEnvironment("Gateway__Registration__AutoResolveIp", "false")
        .WithEnvironment("Gateway__Registration__UseGrpcRegistration", "true")
        .WithEnvironment("Gateway__Registration__BasicAuthUsername", "admin")
        .WithEnvironment("Gateway__Registration__BasicAuthPassword", password)
        .WithEnvironment("Gateway__Registration__HeartbeatIntervalSeconds", "5")
        .WithReference(gateway.GetEndpoint("http"))
        .WithHttpHealthCheck("/health")
        .WaitFor(gateway);
    backends.Add(backend);
    backend.WithEnvironment("Gateway__Registration__DestinationAddress", backend.GetEndpoint("http"));
}

// Opt-in loopback HTTP faults use the existing private topology credential.
if (builder.Configuration.GetValue<bool>("EnableFaultInjection"))
{
    builder.AddProject<Projects.SampleFaultProxy>("fault-proxy", project =>
        { project.ExcludeLaunchProfile = true; project.ExcludeKestrelEndpoints = true; })
        .WithHttpEndpoint(name: "http", env: "FAULT_HTTP_PORT", isProxied: false)
        .WithEnvironment("Fault__Target", backends[0].GetEndpoint("http"))
        .WithEnvironment("Fault__ControlKey", password)
        .WithHttpHealthCheck("/health")
        .WaitFor(backends[0]);
}

if (builder.Configuration.GetValue<bool>("EnableDiscoveryDemo"))
{
    builder.AddProject<Projects.SampleDiscoveryCatalog>("discovery-catalog", project =>
        { project.ExcludeLaunchProfile = true; project.ExcludeKestrelEndpoints = true; })
        .WithHttpEndpoint(name: "http", env: "CATALOG_HTTP_PORT", isProxied: false)
        .WithEnvironment("Catalog__Backends__backend-one", backends[0].GetEndpoint("http"))
        .WithEnvironment("Catalog__Backends__backend-two", backends[1].GetEndpoint("http"))
        .WithEnvironment("Catalog__ControlKey", password)
        .WithHttpHealthCheck("/health")
        .WaitFor(backends[0]).WaitFor(backends[1]);
}

IResourceBuilder<ProjectResource>? secondGateway = null;
if (builder.Configuration.GetValue<bool>("EnableSecondGateway"))
{
    secondGateway = AddGateway("gateway-secondary", false)
        .WithEnvironment("ReverseProxy__Routes__sample-service-path0__ClusterId", "sample-service")
        .WithEnvironment("ReverseProxy__Routes__sample-service-path0__Match__Path", "/api/local-service/{**catch-all}")
        .WithEnvironment("ReverseProxy__Clusters__sample-service__LoadBalancingPolicy", "RoundRobin");
    foreach (var backend in backends)
    {
        secondGateway.WithEnvironment($"ReverseProxy__Clusters__sample-service__Destinations__{backend.Resource.Name}__Address", backend.GetEndpoint("http"))
            .WaitFor(backend);
    }
}

// The default topology needs no container runtime. Opt in with EnableRedis=true.
if (builder.Configuration.GetValue<bool>("EnableRedis"))
{
    var redis = builder.AddRedis("rate-limit");
    if (topology.Legacy) redis.WithDataVolume();
    else redis.WithDataVolume(topology.Volume("rate-limit-data"));
    gateway.WithEnvironment("Gateway__RateLimit__Redis__ConnectionString", redis.Resource.ConnectionStringExpression)
        .WaitFor(redis);
    secondGateway?.WithEnvironment("Gateway__RateLimit__Redis__ConnectionString", redis.Resource.ConnectionStringExpression).WaitFor(redis);
}

foreach (var service in backends.Concat(secondGateway == null ? [gateway] : new[] { gateway, secondGateway }))
{
    // Preserve query redaction and keep AI content out of local telemetry by default.
    service.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "false")
        .WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "false")
        .WithEnvironment("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "false");
}

await builder.Build().RunAsync();
