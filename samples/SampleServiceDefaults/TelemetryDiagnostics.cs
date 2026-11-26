using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>Only the instrumentation registered by SampleServiceDefaults, captured at startup.</summary>
public sealed record SampleTelemetryConfiguration(bool OtlpExporterEnabled)
{
    public string Scope => "SampleServiceDefaults";
    public string[] MetricInstrumentation => ["ASP.NET Core", "HTTP client", "runtime", "YARP", "Wafish metrics", "Wafish compression"];
    public string[] TraceInstrumentation => ["ASP.NET Core", "HTTP client", "application", "YARP"];
    public bool LoggingEnabled => true;
    public bool HealthProbeTracesExcluded => true;
    public bool ConnectivityVerified => false;
}

public static class TelemetryDiagnostics
{
    public static WebApplication MapSampleTelemetryDiagnostics(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("Sample:TelemetryDiagnostics")) return app;
        app.MapGet("/diagnostics/telemetry", (HttpContext context, SampleTelemetryConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
                context.Request.Headers.ContainsKey("Forwarded") || context.Request.Headers.ContainsKey("X-Forwarded-For") || context.Request.Headers.ContainsKey("X-Original-For"))
                return Results.StatusCode(403);
            return Results.Ok(configuration);
        });
        return app;
    }
}
