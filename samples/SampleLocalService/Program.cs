using Wafish.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
if (builder.Configuration.GetValue<bool>("Sample:Orchestrated"))
{
    var port = builder.Configuration.GetValue<int>("BACKEND_HTTP_PORT");
    if (port <= 0)
    {
        throw new InvalidOperationException("Aspire must assign a backend HTTP port.");
    }

    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
}
else
{
    builder.UseYarpKestrelAutoConfig();
}
// Client: one-liner — auto-register on startup, auto-unregister on shutdown
// Minimal config: just GatewayUrl in code or appsettings.json
builder.Services.AddWafishClient();

// Or with code override (higher priority than config file):
// builder.Services.AddWafishClient(options =>
// {
//     options.GatewayUrl = "http://192.168.1.100:5000";
//     options.MatchPath = "/api/my-service/{**catch-all}";
//     options.Order = 50;
// });

// Your business services
builder.Services.AddControllers();

var app = builder.Build();
app.UseRouting();
app.MapControllers();
app.MapDefaultEndpoints();
app.MapGet("/api/local-service/instance", (HttpContext context, IConfiguration configuration) =>
{
    var instance = configuration["Gateway:Registration:InstanceId"] ?? "standalone";
    System.Diagnostics.Activity.Current?.SetTag("gateway.destination.instance", instance);
    context.Response.Headers["X-Backend-Instance"] = instance;
    return Results.Ok(new { instance, traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() });
});

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║  Local service is running                        ║");
Console.WriteLine("║  Flow: client → gateway middleware → YARP → svc  ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");

await app.RunAsync();
