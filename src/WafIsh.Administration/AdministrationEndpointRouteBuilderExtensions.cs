using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wafish.Administration;

/// <summary>Maps controllers and realtime hubs without adding presentation services or static-asset handlers.</summary>
public static class AdministrationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapWafIshAdministration(this IEndpointRouteBuilder endpoints)
    {
        var prefix = endpoints.ServiceProvider.GetRequiredService<IOptions<DashboardOptions>>().Value.RoutePrefix.Trim('/');
        endpoints.MapControllers();
        Protect(endpoints.MapHub<TrafficHub>($"/{prefix}/hubs/traffic"));
        Protect(endpoints.MapHub<OverviewHub>($"/{prefix}/hubs/overview"));
        return endpoints;
    }

    private static void Protect(HubEndpointConventionBuilder endpoints)
        => endpoints.Add(endpoint =>
        {
            var next = endpoint.RequestDelegate!;
            endpoint.RequestDelegate = async context =>
            {
                if (!await context.RequestServices.GetRequiredService<IDashboardAuthorizationService>().IsAuthorizedAsync(context))
                {
                    if (context.Response.StatusCode != 429)
                    {
                        context.Response.StatusCode = 401;
                    }

                    return;
                }
                await next(context);
            };
        });
}
