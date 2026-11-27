using System.Runtime.CompilerServices;
using System.Text.Json;
using Yarp.ReverseProxy.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Services;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Wafish.Plugin.ClaimsTransform;

public sealed class ClaimsTransformProvider : ITransformProvider
{
    private static readonly object ResultKey = new();
    private static readonly ConditionalWeakTable<RouteConfig, CompiledClaimsTransform> Configurations = new();
    private static CompiledClaimsTransform GetConfiguration(RouteConfig route) => Configurations.GetValue(route, r =>
        ClaimsTransformCompiler.Compile(JsonSerializer.Deserialize<ClaimsTransformOptions>(r.Metadata![ClaimsTransformCompiler.MetadataKey], new JsonSerializerOptions(JsonSerializerDefaults.Web))!));
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }
    public void Apply(TransformBuilderContext context)
    {
        if (context.Route.Metadata?.ContainsKey(ClaimsTransformCompiler.MetadataKey) != true) return;
        _ = GetConfiguration(context.Route);
        context.AddRequestTransform(transform =>
        {
            // A missing preflight must fail closed, including hosts with a custom proxy pipeline.
            if (transform.HttpContext.Items[ResultKey] is not ClaimsTransformResult { ErrorStatus: null } result)
            {
                transform.HttpContext.Response.StatusCode = 403;
                return ValueTask.CompletedTask;
            }
            if (result.Path != null) transform.Path = result.Path;
            foreach (var mapping in result.Mappings)
            {
                if (mapping.Target == ClaimTarget.Header)
                {
                    transform.ProxyRequest.Headers.Remove(mapping.Name);
                    transform.ProxyRequest.Content?.Headers.Remove(mapping.Name);
                    if (mapping.Value != null) transform.ProxyRequest.Headers.TryAddWithoutValidation(mapping.Name, mapping.Value);
                }
                else
                {
                    transform.Query.Collection.Remove(mapping.Name);
                    if (mapping.Value != null) transform.Query.Collection[mapping.Name] = mapping.Value;
                }
            }
            return ValueTask.CompletedTask;
        });
    }

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config;
        if (route?.Metadata?.ContainsKey(ClaimsTransformCompiler.MetadataKey) != true)
        { await next(context); return; }
        var result = ClaimsTransformEvaluator.Evaluate(GetConfiguration(route), context.User);
        if (result.ErrorStatus == 401)
        {
            var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
            if (schemes != null && await schemes.GetDefaultChallengeSchemeAsync() != null) await context.ChallengeAsync();
            else context.Response.StatusCode = 401;
            return;
        }
        if (result.ErrorStatus is { } status) { context.Response.StatusCode = status; return; }
        context.Items[ResultKey] = result;
        try { await next(context); }
        finally { context.Items.Remove(ResultKey); }
    }
}
