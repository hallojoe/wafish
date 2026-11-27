using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wafish.Runtime.InternalExecution;

// A private routing provider keeps frozen endpoints out of the host's link-generation data sources.
internal sealed class FrozenRouteMatcher : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly RequestDelegate _match;
    public FrozenRouteMatcher(IServiceProvider host, IReadOnlyList<Endpoint> endpoints)
    {
        var services=new ServiceCollection();
        services.AddLogging(); services.AddRouting();
        services.AddSingleton(host.GetRequiredService<ILoggerFactory>());
        services.AddSingleton<DiagnosticListener>(_=>new("Wafish.InternalRouting"));
        var constraints=host.GetRequiredService<IOptions<RouteOptions>>().Value.ConstraintMap;
        services.Configure<RouteOptions>(o=> { foreach(var pair in constraints) o.ConstraintMap[pair.Key]=pair.Value; });
        foreach(var registration in services.Where(s=>s.ServiceType==typeof(MatcherPolicy)).ToArray()) services.Remove(registration);
        foreach(var policy in host.GetServices<MatcherPolicy>()) services.AddSingleton(policy);
        services.AddSingleton(host.GetRequiredService<ParameterPolicyFactory>());
        services.AddSingleton(host.GetRequiredService<EndpointSelector>());
        _services=services.BuildServiceProvider();
        try
        {
        var app=new ApplicationBuilder(_services);
        app.UseRouting();
        app.Run(_=>Task.CompletedTask);
        app.UseEndpoints(routes=>routes.DataSources.Add(new DefaultEndpointDataSource(endpoints)));
        _match=app.Build();
        }
        catch { _services.Dispose();throw; }
    }
    public Task MatchAsync(HttpContext context)=>_match(context);
    public void Dispose()=>_services.Dispose();
}
