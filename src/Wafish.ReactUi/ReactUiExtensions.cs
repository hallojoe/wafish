using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Wafish.Administration;
using Wafish.Dashboard.Infrastructure;
using Wafish.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wafish.ReactUi;

public sealed class ReactUiOptions
{
    public string RoutePrefix { get; set; } = "console";
}

/// <summary>Hosts the compiled React client. Administration APIs are mapped separately.</summary>
public static class ReactUiExtensions
{
    public static IServiceCollection AddWafishReactUi(this IServiceCollection services, Action<ReactUiOptions>? configure = null)
    {
        services.AddWafishAdministration();
        services.AddOptions<ReactUiOptions>().BindConfiguration("Gateway:ReactUi")
            .Validate(o => o.RoutePrefix.Trim('/').Length > 0 &&
                o.RoutePrefix.Trim('/').Split('/').All(s => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')),
                "React UI prefix must contain valid path segments.").ValidateOnStart();
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddOptions<Wafish.Infrastructure.Middleware.GatewayMiddlewareOptions>()
            .Configure<IOptions<ReactUiOptions>>((runtime, options) =>
                runtime.AdditionalAdministrationPrefixes = runtime.AdditionalAdministrationPrefixes.Append(options.Value.RoutePrefix).Distinct().ToArray());
        return services;
    }

    public static IEndpointRouteBuilder MapWafishReactUi(this IEndpointRouteBuilder endpoints)
    {
        var ui = endpoints.ServiceProvider.GetRequiredService<IOptions<ReactUiOptions>>().Value;
        var dashboard = endpoints.ServiceProvider.GetRequiredService<IOptions<DashboardOptions>>().Value;
        var uiBase = "/" + ui.RoutePrefix.Trim('/');
        var apiBase = "/" + dashboard.RoutePrefix.Trim('/');
        if (uiBase.Equals(apiBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("React UI and administration API prefixes must be different.");
        }

        var assembly = typeof(ReactUiExtensions).Assembly;
        var contentTypes = new FileExtensionContentTypeProvider();
        var assets = assembly.GetManifestResourceNames().Where(n => n.StartsWith("ReactUi/", StringComparison.Ordinal))
            .ToDictionary(n => n["ReactUi/".Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);
        endpoints.MapGet(uiBase + "/version", (HttpContext context) =>
        {
            var policy = context.RequestServices.GetRequiredService<ControlPlaneAccessPolicy>();
            if (!policy.IsAllowed(context, dashboard.Auth.AuthMode == DashboardAuthMode.None)) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(ReactUiBuildInfo.Current);
        });
        endpoints.MapGet(uiBase + "/assets/{**asset}", async context =>
        {
            var file = "assets/" + context.Request.RouteValues["asset"];
            if (!assets.TryGetValue(file, out var resource)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = contentTypes.TryGetContentType(file, out var type) ? type : "application/octet-stream";
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        async Task Shell(HttpContext context)
        {
            var policy = context.RequestServices.GetRequiredService<ControlPlaneAccessPolicy>();
            if (!policy.IsAllowed(context, dashboard.Auth.AuthMode == DashboardAuthMode.None))
            { context.Response.StatusCode = 403; return; }
            if (!assets.TryGetValue("index.html", out var index))
            {
                throw new InvalidOperationException("React assets are missing. Run npm ci and npm run build in ClientApp.");
            }

            await using var stream = assembly.GetManifestResourceStream(index)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var features = context.RequestServices.GetRequiredService<AdministrationFeatures>();
            var configuration = JsonSerializer.Serialize(new
            {
                uiBase = context.Request.PathBase.Add(new PathString(uiBase)).ToUriComponent(),
                apiBase = context.Request.PathBase.Add(new PathString(apiBase)).ToUriComponent(), authMode = dashboard.Auth.AuthMode.ToString(),
                apiKeyHeader = dashboard.Auth.ApiKeyHeaderName, locale = "en-US",
                ai = features.Ai, mcp = features.Mcp, webAuthn = dashboard.Auth.WebAuthn.Enabled, build = ReactUiBuildInfo.Current
            });
            var html = (await reader.ReadToEndAsync(context.RequestAborted))
                .Replace("<!--APP_CONFIG-->", $"<base href=\"{HtmlEncoder.Default.Encode(context.Request.PathBase.Add(new PathString(uiBase)).ToUriComponent())}/\"><script type=\"application/json\" id=\"app-config\">{configuration}</script>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await context.Response.WriteAsync(html, context.RequestAborted);
        }
        endpoints.MapGet(uiBase, Shell);
        endpoints.MapGet(uiBase + "/{**path}", Shell).WithOrder(int.MaxValue - 1);
        return endpoints;
    }
}
