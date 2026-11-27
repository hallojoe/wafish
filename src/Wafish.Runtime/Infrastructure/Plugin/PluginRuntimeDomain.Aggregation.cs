using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure.Yarp;
using Wafish.Plugin.Aggregation;
using Wafish.Plugins;
using Wafish.Services;
using Yarp.ReverseProxy.Model;

namespace Wafish.Dashboard.Infrastructure.Plugin;

public sealed partial class PluginRuntimeDomainManager
{
    private static readonly object AggregateConfigKey=new();
    internal Task InvokeAggregationPipelineAsync(HttpContext context,RequestDelegate next)=>
        InvokeCurrentPipelineAsync(context,domain=>domain.AggregationPipeline,next);
    private static RequestDelegate BuildAggregationPipeline(IServiceProvider services,Dictionary<string,IGatewayPlugin> plugins)
    {
        if(!plugins.ContainsKey("request-aggregation")) return ContinueHostPipelineAsync;
        var builder=new ReverseProxyApplicationBuilderAdapter(new ApplicationBuilder(services));
        var snapshots=services.GetRequiredService<IGatewaySnapshotPublisher>();
        builder.Use(async(context,next)=>
        {
            var routeId=context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
            var snapshot=RequestExecutionSnapshot.Current ?? snapshots.Current;
            if(routeId==null || !snapshot.TryGetRouteExecutionPlan(routeId,out var plan) ||
                plan!.Plugins.FirstOrDefault(p=>p.Binding.PluginId.Equals("request-aggregation",StringComparison.OrdinalIgnoreCase)) is not {} aggregate)
            {await ContinueHostPipelineAsync(context);return;}
            context.Response.OnStarting(()=> {context.Response.Headers.CacheControl="no-store";return Task.CompletedTask;});
            context.Items[AggregateConfigKey]=aggregate.GetRuntimeConfig<CompiledAggregation>();
            try {await next(context);}finally {context.Items.Remove(AggregateConfigKey);}
        });
        builder.UseMiddleware<BuiltinTransformMiddleware>();
        foreach(var id in new[]{"proxy-log","traffic-metrics","rate-limit","rate-limit-redis"})
            if(plugins.TryGetValue(id,out var policy)) policy.ConfigureProxyPipeline(builder);
        builder.Run(context=>services.GetRequiredService<AggregationHandler>().InvokeAsync(context,(CompiledAggregation)context.Items[AggregateConfigKey]!));
        return builder.Build();
    }
}
