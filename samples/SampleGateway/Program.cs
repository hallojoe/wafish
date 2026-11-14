using Wafish.ReactUi;
using SampleGateway;
using Wafish.Administration;
using Wafish.Extensions;
using Wafish.Runtime;
using Wafish.Dashboard.Infrastructure.Deployment;
using Wafish.Storage.Sqlite;
using Microsoft.Extensions.Options;
using Serilog;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("Yarp.ReverseProxy", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[Serilog] {Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting SampleGateway with Serilog...");
    
    var builder = WebApplication.CreateBuilder(args);

    // Replace default logger with Serilog
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger);
    builder.AddServiceDefaults();

    // Enable HTTP/2 on all endpoints (required for gRPC)
    // When Kestrel:Endpoints has 2+ entries, auto-injection of the +1 gRPC port is
    // skipped automatically, so role-based routing keeps working.
    if (builder.Configuration.GetValue<bool>("Sample:Orchestrated"))
    {
        var httpPort = builder.Configuration.GetValue<int>("GATEWAY_HTTP_PORT");
        var grpcPort = builder.Configuration.GetValue<int>("GATEWAY_GRPC_PORT");
        if (httpPort <= 0 || grpcPort <= 0)
        {
            throw new InvalidOperationException("Aspire must assign HTTP and gRPC ports.");
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Gateway:Url"] = $"http://127.0.0.1:{httpPort}",
            ["Kestrel:Endpoints:Gateway:Protocols"] = "Http1",
            ["Kestrel:Endpoints:Grpc:Url"] = $"http://127.0.0.1:{grpcPort}",
            ["Kestrel:Endpoints:Grpc:Protocols"] = "Http2"
        });
    }
    else
    {
        builder.UseYarpKestrelAutoConfig();
    }

    // Gateway: one-liner — auto-loads ReverseProxy routes/clusters + dynamic config
    builder.Services.AddWafish(enableRegistration: builder.Configuration.GetValue<bool>("Gateway:Registration:Enabled"));

    // Storage backend: host app is responsible for choosing the storage implementation
    builder.Services.AddWafishStorage();

    // React administration uses the configured authentication policy.
    builder.Services.AddWafishReactUi();
    // Explicitly include the sample AI tools and configuration UI. MCP remains unregistered.
    builder.Services.AddWafishAI();

    // Multi-port / split / proxy-only deployment support (optional, backward compatible)
    // Wires up deployment options, endpoint role resolver, validators, hot-reload, etc.
    builder.Services.AddWafishAdministrationDeployment();

    // Optional: enable gRPC endpoint auth (shares Dashboard credentials by default)
    // builder.Services.AddGatewayApiAuth();

    var app = builder.Build();
    app.UseRouting();
    app.UseCors();

    // Register gateway middleware and proxy routing (mode-aware).
    // Deployment-aware routing and health-check middleware are mounted automatically when deployment services are registered.
    app.UseWafishGateway(configureAdministration: _ =>
    {
        app.MapWafishAdministration();
        app.MapWafishReactUi();
    });
    app.MapWafishGrpc(); // gRPC GatewayRegistry service (HTTP/2)

    // Log startup summary
    var depOptions = app.Services.GetRequiredService<IOptions<DeploymentOptions>>().Value;
    var grpcEndpoint = depOptions.ResolvedEndpoints.FirstOrDefault(e =>
        e.EndpointName.Contains("grpc", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine("======================================================");
    Console.WriteLine("  Internal gateway is running");
    Console.WriteLine($"  Mode:               {depOptions.Mode}");
    foreach (var ep in depOptions.ResolvedEndpoints)
    {
        Console.WriteLine($"  Endpoint:           {ep.EndpointName} → {ep.IpAddress}:{ep.Port} ({ep.Role})");
    }

    Console.WriteLine("  Credentials:        supplied through host configuration");
    Console.WriteLine("  Health:             /health, /ready, /live");
    Console.WriteLine("  Logger:             Serilog");
    Console.WriteLine(grpcEndpoint != null
        ? $"  gRPC:               HTTP/2 listening on {grpcEndpoint.IpAddress}:{grpcEndpoint.Port}"
        : "  gRPC:               HTTP/2 enabled");
    Console.WriteLine("======================================================");

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var prefix = app.Services.GetRequiredService<IOptions<ReactUiOptions>>().Value.RoutePrefix;
        var dashboardUrl = OperatorUrl.Resolve(app.Urls, depOptions.ResolvedEndpoints, prefix);
        if (dashboardUrl is null) return;
        Console.WriteLine($"  Dashboard:          {dashboardUrl}");
        if (app.Environment.IsDevelopment()
            && !app.Configuration.GetValue<bool>("Sample:Orchestrated")
            && Environment.GetEnvironmentVariable("ASPNETCORE_AUTO_BROWSER") != "0")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dashboardUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception error) { Log.Warning(error, "Failed to open browser at {Url}", dashboardUrl); }
        }
    });

    Log.Information("Gateway started successfully (waiting for Kestrel to bind ports...)");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Gateway terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
