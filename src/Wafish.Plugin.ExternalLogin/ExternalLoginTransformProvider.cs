using System.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Wafish.Plugin.ExternalLogin;

public sealed class ExternalLoginTransformProvider : ITransformProvider
{
    private readonly bool _enabled;
    public ExternalLoginTransformProvider() { }
    public ExternalLoginTransformProvider(ExternalLoginCatalog catalog) => _enabled = true;
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }
    public void Apply(TransformBuilderContext context)
    {
        var binding = ExternalLoginCompiler.Read(context.Route);
        if (binding == null && !_enabled) return;
        context.AddRequestTransform(transform =>
        {
            transform.ProxyRequest.Headers.Remove(ExternalLoginRuntime.CsrfHeader);
            if (binding == null)
            {
                if (transform.HttpContext.Features.Get<Wafish.Plugins.GatewayVisitorIdentityFeature>() != null)
                {
                    transform.ProxyRequest.Headers.Remove("Cookie");
                    transform.ProxyRequest.Headers.Remove("Authorization");
                    transform.ProxyRequest.Headers.Remove("Proxy-Authorization");
                }
                else if (transform.ProxyRequest.Headers.TryGetValues("Cookie", out var values))
                {
                    var remaining = string.Join("; ", values.SelectMany(v => v.Split(';')).Where(v => !OwnedCookie(v)).Select(v => v.Trim()));
                    transform.ProxyRequest.Headers.Remove("Cookie");
                    if (remaining.Length > 0) transform.ProxyRequest.Headers.TryAddWithoutValidation("Cookie", remaining);
                }
                return ValueTask.CompletedTask;
            }
            var visitor = transform.HttpContext.Features.Get<VisitorRequestFeature>();
            if (visitor == null) { transform.HttpContext.Response.StatusCode = 403; return ValueTask.CompletedTask; }
            transform.ProxyRequest.Headers.Remove("Cookie");
            transform.ProxyRequest.Headers.Remove("Authorization");
            transform.ProxyRequest.Headers.Remove("Proxy-Authorization");
            transform.ProxyRequest.Headers.Remove(ExternalLoginRuntime.CsrfHeader);
            if (visitor.Target != null)
            {
                var destination = transform.ProxyRequest.RequestUri ?? new Uri(transform.DestinationPrefix);
                if (!visitor.Target.Origins.Contains(destination.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal) || string.IsNullOrEmpty(visitor.AccessToken))
                { transform.HttpContext.Response.StatusCode = 403; return ValueTask.CompletedTask; }
                transform.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", visitor.AccessToken);
            }
            return ValueTask.CompletedTask;
        });
        context.AddResponseTransform(transform =>
        {
            if (binding != null) transform.HttpContext.Response.Headers.Remove("Set-Cookie");
            else if (transform.HttpContext.Response.Headers.TryGetValue("Set-Cookie", out var cookies))
                transform.HttpContext.Response.Headers.SetCookie = new Microsoft.Extensions.Primitives.StringValues(cookies.Where(c => c != null && !OwnedCookie(c)).ToArray());
            return ValueTask.CompletedTask;
        });
    }
    private static bool OwnedCookie(string value) => value.TrimStart().StartsWith("__Host-wafish-visitor-", StringComparison.OrdinalIgnoreCase);
}
