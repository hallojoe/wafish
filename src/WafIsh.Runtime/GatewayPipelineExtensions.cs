using Microsoft.AspNetCore.Http;
using WafIsh.Dashboard.Infrastructure.Deployment;
using WafIsh.Dashboard.Infrastructure.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WafIsh.Runtime;

/// <summary>Controls gateway middleware independently of administration presentation.</summary>
public class GatewayPipelineOptions
{
    public bool? AutoUseMiddleware { get; set; }
    public bool UseDeploymentMiddleware { get; set; } = true;
    public bool UseBuiltInProxyPipeline { get; set; } = true;
    public bool AutoUseCors { get; set; } = true;
    public bool AutoUseAuthorization { get; set; } = true;
}

public static class GatewayPipelineExtensions
{
    private const string ConfiguredKey = "__WafIshGateway_Configured";

    /// <summary>Composes gateway middleware. The optional administration callback adds presentation at its existing pipeline position.</summary>
    public static IApplicationBuilder UseWafIshGateway(this IApplicationBuilder app,
        GatewayPipelineOptions? options = null,
        Action<IApplicationBuilder>? configureAdministration = null,
        Action<IReverseProxyApplicationBuilder>? configureProxy = null)
    {
        if (app.Properties.ContainsKey(ConfiguredKey)) return app;
        app.Properties[ConfiguredKey] = true;
        options ??= new();
        var deployment = app.ApplicationServices.GetRequiredService<IOptions<DeploymentOptions>>().Value;
        // Resolve endpoint roles before using the normalized deployment mode.
        var resolver = app.ApplicationServices.GetService<EndpointRoleResolver>();
        var auto = options.AutoUseMiddleware ?? deployment.AutoUseMiddleware;
        if (options.AutoUseCors) app.UseCors();
        if (options.AutoUseAuthorization) app.UseAuthorization();
        if (auto && options.UseDeploymentMiddleware)
        {
            if (resolver != null && deployment.Mode != DeploymentMode.AllInOne)
                app.UseMiddleware<EndpointRouterMiddleware>();
            if (deployment.HealthCheck.Enabled) app.UseMiddleware<HealthCheckMiddleware>();
        }
        var middleware = app.ApplicationServices.GetRequiredService<IOptions<WafIsh.Infrastructure.Middleware.GatewayMiddlewareOptions>>().Value;
        var prefixes = middleware.AdditionalAdministrationPrefixes.Append(middleware.RoutePrefix)
            .Select(prefix => "/" + prefix.Trim('/')).ToArray();
        // Proxy compression belongs inside the response-cache boundary.
        // Administration pages and assets can use the framework compression middleware.
        app.UseWhen(context => context.GetEndpoint()?.Metadata.GetMetadata<global::Yarp.ReverseProxy.Model.RouteModel>() == null &&
            prefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)), branch => branch.UseResponseCompression());
        if (deployment.Mode != DeploymentMode.ProxyOnly) configureAdministration?.Invoke(app);
        if (auto) app.UseWafIshRuntime();
        if (deployment.Mode != DeploymentMode.DashboardOnly && app is IEndpointRouteBuilder endpoints)
            endpoints.MapWafIshProxy(configureProxy, auto && options.UseBuiltInProxyPipeline);
        return app;
    }
}
