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
    public static IEndpointRouteBuilder MapWafishAdministration(this IEndpointRouteBuilder endpoints)
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
                var authorization = context.RequestServices.GetRequiredService<IDashboardAuthorizationService>();
                if (!await authorization.IsAuthorizedAsync(context))
                {
                    if (context.Response.StatusCode != 429)
                    {
                        context.Response.StatusCode = 401;
                    }

                    return;
                }
                if (!await context.RequestServices.GetRequiredService<IDashboardPermissionService>().IsAllowedAsync(context,
                    DashboardPermission.Read, "Realtime.Connect", context.RequestAborted))
                { context.Response.StatusCode = 403; return; }
                var options = context.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
                var sessions = context.RequestServices.GetRequiredService<DashboardSessionStore>();
                var lifetime = options.AuthorizeRequest == null && options.AuthMode is DashboardAuthMode.DefaultJwt or DashboardAuthMode.CustomJwt
                    && authorization is DashboardAuthorizationService standard
                    ? sessions.Lifetime(standard.ExtractJwtToken(context)) : CancellationToken.None;
                using var revocation = lifetime.Register(context.Abort);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                var monitor = RevalidateAsync(context, authorization, clock, cancellation.Token);
                try { await next(context); }
                finally
                {
                    await cancellation.CancelAsync();
                    await monitor;
                }
            };
        });

    private static async Task RevalidateAsync(Microsoft.AspNetCore.Http.HttpContext context,
        IDashboardAuthorizationService authorization, TimeProvider clock, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
                if (!await authorization.IsAuthorizedAsync(context) || !await context.RequestServices.GetRequiredService<IDashboardPermissionService>()
                    .IsAllowedAsync(context, DashboardPermission.Read, "Realtime.Subscribe", cancellationToken)) { context.Abort(); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { context.Abort(); }
    }
}
