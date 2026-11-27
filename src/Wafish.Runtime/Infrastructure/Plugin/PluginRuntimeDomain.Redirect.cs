using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure.Yarp;
using Wafish.Plugin.Redirect;
using Wafish.Plugins;
using Wafish.Services;
using Yarp.ReverseProxy.Model;

namespace Wafish.Dashboard.Infrastructure.Plugin;

public sealed partial class PluginRuntimeDomainManager
{
    private static readonly object RedirectResultKey = new();
    private sealed record RedirectAttempt(RedirectResult Result, long Started);

    internal Task InvokeRedirectPipelineAsync(HttpContext context, RequestDelegate next) =>
        InvokeCurrentPipelineAsync(context, domain => domain.RedirectPipeline, next);

    private static RequestDelegate BuildRedirectPipeline(IServiceProvider services, Dictionary<string, IGatewayPlugin> plugins)
    {
        if (!plugins.ContainsKey("redirect")) return ContinueHostPipelineAsync;
        var builder = new ReverseProxyApplicationBuilderAdapter(new ApplicationBuilder(services));
        var snapshots = services.GetRequiredService<IGatewaySnapshotPublisher>();
        builder.Use(async (context, next) =>
        {
            var routeId = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
            var snapshot = RequestExecutionSnapshot.Current ?? snapshots.Current;
            if (routeId is null || !snapshot.TryGetRouteExecutionPlan(routeId, out var plan) ||
                plan!.Plugins.FirstOrDefault(p => p.Binding.PluginId.Equals("redirect", StringComparison.OrdinalIgnoreCase)) is not { } plugin)
            {
                await ContinueHostPipelineAsync(context);
                return;
            }
            var started = Stopwatch.GetTimestamp();
            var result = RedirectEvaluator.Evaluate(plugin.GetRuntimeConfig<CompiledRedirect>(), context.Request);
            if (result.Outcome == "PassThrough")
            {
                await ContinueHostPipelineAsync(context);
                return;
            }
            context.Items[RedirectResultKey] = new RedirectAttempt(result, started);
            try { await next(context); }
            finally { context.Items.Remove(RedirectResultKey); }
        });
        builder.UseMiddleware<BuiltinTransformMiddleware>();
        // These middleware resolve route bindings from endpoint metadata before YARP initializes a feature.
        foreach (var id in new[] { "proxy-log", "traffic-metrics", "rate-limit", "rate-limit-redis" })
            if (plugins.TryGetValue(id, out var policy)) policy.ConfigureProxyPipeline(builder);
        builder.Run(context =>
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var attempt = (RedirectAttempt)context.Items[RedirectResultKey]!;
            var result = attempt.Result;
            context.Response.StatusCode = result.StatusCode!.Value;
            if (result.Location is not null) context.Response.Headers.Location = result.Location;
            context.Response.ContentLength = 0;
            services.GetService<IPluginResourceMonitor>()?.RecordRequest("redirect",
                (long)Stopwatch.GetElapsedTime(attempt.Started).TotalMilliseconds, result.Outcome == "Redirect");
            return Task.CompletedTask;
        });
        return builder.Build();
    }
}
