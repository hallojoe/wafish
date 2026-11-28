using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Wafish.Plugin.RouteAuthorization;

public static class RouteAuthorizationMiddleware
{
    private static readonly ConditionalWeakTable<RouteConfig, AuthorizationPolicy> Policies = new();
    private static readonly Meter Meter = new("Wafish.RouteAuthorization");
    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>("wafish.route_authorization.decisions");
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config;
        if (route?.Metadata?.ContainsKey(RouteAuthorizationCompiler.MetadataKey) != true) { await next(context); return; }
        var policy = Prepare(route);
        var result = await context.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User, context, policy);
        Decisions.Add(1, new KeyValuePair<string,object?>("outcome",result.Succeeded ? "allowed" : "denied"));
        if (result.Succeeded) { await next(context); return; }
        await RejectAsync(context);
    }
    internal static AuthorizationPolicy Prepare(RouteConfig route) => Policies.GetValue(route, r =>
        {
            var compiled = RouteAuthorizationCompiler.Compile(JsonSerializer.Deserialize<RouteAuthorizationOptions>(r.Metadata![RouteAuthorizationCompiler.MetadataKey],RouteAuthorizationCompiler.Json)!);
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(c => compiled.Allows(c.User)).Build();
        });
    private static async Task RejectAsync(HttpContext context)
    {
        if (context.Features.Get<Wafish.Plugins.GatewayVisitorIdentityFeature>() != null)
        { context.Response.StatusCode = 403; return; }
        var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
        if (context.User.Identities.Any(i => i.IsAuthenticated))
        {
            if (schemes != null && await schemes.GetDefaultForbidSchemeAsync() != null) await context.ForbidAsync();
            else context.Response.StatusCode=403;
        }
        else
        {
            if (schemes != null && await schemes.GetDefaultChallengeSchemeAsync() != null) await context.ChallengeAsync();
            else context.Response.StatusCode=401;
        }
    }
}
