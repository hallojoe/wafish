using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugin.Redirect;
using RedirectResult = Wafish.Plugin.Redirect.RedirectResult;
using Wafish.Services;
using Wafish.Storage.Entities;
using Wafish.Storage;
using Yarp.ReverseProxy.Model;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

public sealed record RedirectPreviewRequest(SavePluginBindingRequest Binding, RedirectSampleRequest Request);
public sealed record RedirectSampleRequest(string Url, string Method = "GET", string PathBase = "", Dictionary<string, string>? Headers = null);
public sealed record RedirectPreviewResponse(string RouteId, string? MatchedRouteId, long ConfigurationRevision, RedirectResult Result);

[ApiController]
[Route("api/plugins/redirect")]
public sealed class RedirectPreviewController(IGatewaySnapshotPublisher snapshots, IRouteRepository routes,
    IPluginConfigurationSchemaValidator schemas) : ControllerBase
{
    /// <summary>Matches a sample request and evaluates a candidate redirect without executing its policies.</summary>
    [HttpPost("preview")]
    [DashboardPermission(DashboardPermission.Read)]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> PreviewRedirect(RedirectPreviewRequest request, CancellationToken ct)
    {
        if (request.Binding is null || request.Request is null ||
            !string.Equals(request.Binding.PluginId, "redirect", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = 400, message = "A redirect binding and sample request are required." });
        if (!string.Equals(request.Binding.Scope, "Route", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.Binding.ScopeId))
            return BadRequest(new { code = 400, message = "Redirects require Route scope." });
        var storedRoutes = await routes.GetAllRoutesAsync(ct);
        var target = storedRoutes.FirstOrDefault(r => r.RouteUid == request.Binding.ScopeId || r.RouteId == request.Binding.ScopeId);
        var current = snapshots.Current;
        var route = current.Routes.FirstOrDefault(r => r.RouteId == (target?.RouteId ?? request.Binding.ScopeId));
        if (route is null) return BadRequest(new { code = 400, message = "Redirect route is not active." });
        try
        {
            var schema = new RedirectPlugin().Manifest.Schemas.Single(s => s.Version == request.Binding.SchemaVersion);
            if (!schemas.TryValidate(request.Binding.ConfigJson ?? "", schema.ConfigJsonSchema, out var configJson, out var error))
                return BadRequest(new { code = 400, message = error });
            var candidate = new PluginBindingSnapshot("preview", "redirect", PluginBindingScope.Route, route.RouteId, configJson,
                request.Binding.SchemaVersion, 0, request.Binding.Order);
            var compiled = new RedirectConfigurationCompiler().Compile(candidate, route).GetRuntimeConfig<CompiledRedirect>();
            var sample = request.Request;
            if (sample.Url is null || sample.Url.Length > RedirectEvaluator.MaximumUrlBytes ||
                !Uri.TryCreate(sample.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
                url.UserInfo.Length != 0 || url.Fragment.Length != 0 || string.IsNullOrWhiteSpace(sample.Method) ||
                sample.Method.Any(c => !char.IsAsciiLetter(c)) || sample.Headers?.Count > 64)
                throw new ArgumentException("Use a bounded HTTP(S) sample URL and an HTTP method.");
            var context = new DefaultHttpContext { RequestServices = HttpContext.RequestServices, RequestAborted = ct };
            context.Request.Scheme = url.Scheme;
            context.Request.Host = HostString.FromUriComponent(url);
            var path = PathString.FromUriComponent(url.AbsolutePath);
            var pathBase = new PathString(sample.PathBase ?? "");
            if (!path.StartsWithSegments(pathBase, out var remaining))
                throw new ArgumentException("The sample URL must include its path base.");
            context.Request.PathBase = pathBase;
            context.Request.Path = remaining;
            context.Request.QueryString = new QueryString(url.Query);
            context.Request.Method = sample.Method.ToUpperInvariant();
            foreach (var header in sample.Headers ?? [])
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Set the sample host through its URL.");
                context.Request.Headers[header.Key] = header.Value;
            }
            // Routing uses the actual endpoint metadata, including YARP host/header/query matcher policies.
            // Keep preview data sources private. UseEndpoints would append them to the host's global route inventory.
            var endpoints = HttpContext.RequestServices.GetRequiredService<EndpointDataSource>().Endpoints
                .Where(e => e.Metadata.GetMetadata<RouteModel>() is not null).ToArray();
            var app = new ApplicationBuilder(HttpContext.RequestServices);
            app.UseRouting();
            var previewRoutes = app.Properties.Values.OfType<IEndpointRouteBuilder>().Single();
            previewRoutes.DataSources.Add(new DefaultEndpointDataSource(endpoints));
            app.Run(_ => Task.CompletedTask);
            await app.Build()(context);
            var matchedId = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
            var result = matchedId != route.RouteId ? new RedirectResult("RouteMismatch") :
                !request.Binding.Enabled ? new RedirectResult("PassThrough") : RedirectEvaluator.Evaluate(compiled, context.Request);
            return Ok(new { code = 200, data = new RedirectPreviewResponse(route.RouteId, matchedId, current.Version, result) });
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException ||
            ex.GetType().FullName == "Microsoft.AspNetCore.Routing.Matching.AmbiguousMatchException")
        {
            return BadRequest(new { code = 400, message = ex.Message });
        }
    }
}
