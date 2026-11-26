using Wafish.Extensions;
using SampleLocalService;

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
var identity = SampleInstanceIdentity.Resolve(builder.Configuration);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton<SampleStreamActivity>();
builder.Services.AddSingleton(new SampleOrderStore(
    builder.Configuration["Sample:OrdersStatePath"] ?? Path.Combine(AppContext.BaseDirectory, "sample-orders.json"), TimeProvider.System));
builder.Services.AddWafishClient(options => options.InstanceId = identity.Value);

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
app.Use(async (context, next) => { identity.Apply(context); await next(context); });
app.MapControllers();
app.MapDefaultEndpoints();
app.MapGet("/api/local-service/instance", (SampleInstanceIdentity instance) =>
{
    return Results.Ok(new { instance = instance.Value, traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() });
});

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║  Local service is running                        ║");
Console.WriteLine("║  Flow: client → gateway middleware → YARP → svc  ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");

await app.RunAsync();
