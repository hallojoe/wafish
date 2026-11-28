using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Yarp.ReverseProxy.Model;

namespace Wafish.Plugin.ExternalLogin;

public sealed class ExternalLoginRuntime : IDisposable
{
    internal const string Prefix = "/_wafish/auth";
    internal const string CsrfHeader = "X-Wafish-CSRF";
    private const string CsrfKey = ".wafish-csrf";
    private static readonly Meter Meter = new("Wafish.ExternalLogin");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("wafish.external_login.outcomes");
    private readonly ExternalLoginCatalog _catalog;
    private readonly RedisVisitorSessionStore _sessions;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidc;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _clock;
    // Fixed partition count bounds attacker-controlled address cardinality.
    private readonly System.Threading.RateLimiting.TokenBucketRateLimiter[] _limits = Enumerable.Range(0, 256).Select(_ =>
        new System.Threading.RateLimiting.TokenBucketRateLimiter(new() { TokenLimit = 20, TokensPerPeriod = 20, ReplenishmentPeriod = TimeSpan.FromMinutes(1), AutoReplenishment = false, QueueLimit = 0 })).ToArray();
    internal ExternalLoginRuntime(ExternalLoginCatalog catalog, RedisVisitorSessionStore sessions,
        IOptionsMonitor<OpenIdConnectOptions> oidc, IHttpClientFactory clients, TimeProvider clock)
    { _catalog = catalog; _sessions = sessions; _oidc = oidc; _clients = clients; _clock = clock; }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var reserved = context.Request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!reserved) { await next(context); return; }
            NoStore(context);
            if (context.Request.ContentLength > 65536) { context.Response.StatusCode = 413; return; }
            var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 65536;
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.OnStarting(() => { context.Response.Headers.Remove("Access-Control-Allow-Origin"); context.Response.Headers.Remove("Access-Control-Allow-Credentials"); return Task.CompletedTask; });
            var site = _catalog.ForOrigin(Origin(context.Request));
            if (site == null || context.Request.PathBase.HasValue) { context.Response.StatusCode = 404; return; }
            var limiter = _limits[(context.Connection.RemoteIpAddress?.GetHashCode() ?? 0) & 255];
            limiter.TryReplenish();
            using var lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "60"; return; }
            var path = context.Request.Path.Value!;
            if (site.Providers.Any(p => path == ExternalLoginCatalog.Callback(site.Id, p.Id)))
            { await next(context); return; }
            if (path == Prefix + "/session" && HttpMethods.IsGet(context.Request.Method))
            {
                var session = await AuthenticateAsync(context, site);
                await context.Response.WriteAsJsonAsync(session == null ? new { authenticated = false, name = (string?)null, csrfToken = (string?)null } :
                    new { authenticated = true, name = session.Ticket.Principal.FindFirst("name")?.Value, csrfToken = session.Ticket.Properties.Items.GetItem(CsrfKey) }, context.RequestAborted);
                return;
            }
            if (path == Prefix + "/logout" && HttpMethods.IsPost(context.Request.Method))
            {
                var session = await AuthenticateAsync(context, site);
                if (session == null) { context.Response.StatusCode = 401; return; }
                if (!CsrfValid(context, site, session)) { context.Response.StatusCode = 403; return; }
                await _sessions.RemoveAsync(session.Key);
                await context.SignOutAsync(ExternalLoginCatalog.CookieScheme(site.Id));
                context.Response.StatusCode = 204; Count("logout"); return;
            }
            if ((path == Prefix + "/login" || path.StartsWith(Prefix + "/login/", StringComparison.Ordinal)) && HttpMethods.IsGet(context.Request.Method))
            {
                var returnUrl = context.Request.Query["returnUrl"].ToString();
                if (returnUrl.Length == 0) returnUrl = "/";
                if (!LocalReturn(returnUrl)) { context.Response.StatusCode = 400; return; }
                var id = path == Prefix + "/login" ? null : path[(Prefix.Length + 7)..];
                var provider = id == null && site.Providers.Length == 1 ? site.Providers[0] : site.Providers.SingleOrDefault(p => p.Id == id);
                if (provider != null)
                {
                    Count("challenge");
                    var properties = new AuthenticationProperties { RedirectUri = returnUrl };
                    properties.Items[CsrfKey] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    await context.ChallengeAsync(ExternalLoginCatalog.OidcScheme(site.Id, provider.Id), properties);
                    return;
                }
                if (id != null) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "text/html; charset=utf-8";
                var links = site.Providers.Select(p => $"<li><a href=\"{Prefix}/login/{p.Id}?returnUrl={WebUtility.HtmlEncode(Uri.EscapeDataString(returnUrl))}\">{WebUtility.HtmlEncode(string.IsNullOrEmpty(p.DisplayName) ? p.Id : p.DisplayName)}</a></li>");
                await context.Response.WriteAsync("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Sign in</title><h1>Sign in</h1><ul>" + string.Join("", links) + "</ul></html>");
                return;
            }
            context.Response.StatusCode = 404;
        }
        catch (Exception ex) when (ex is RedisException or HttpRequestException or TimeoutException || ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested || reserved && ex is Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectProtocolException or InvalidOperationException)
        {
            Count("unavailable");
            if (context.Response.HasStarted) throw;
            context.Response.Clear(); NoStore(context); context.Response.StatusCode = 503;
        }
    }

    public async Task AuthorizeAsync(HttpContext context, RequestDelegate next, bool internalRequest = false)
    {
        var binding = ExternalLoginCompiler.Read(context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config);
        if (binding == null) { await next(context); return; }
        NoStore(context);
        var site = _catalog.Site(binding.Site);
        if (Origin(context.Request) != site.Origin || context.Request.PathBase.HasValue) { context.Response.StatusCode = 403; return; }
        var session = await AuthenticateAsync(context, site);
        if (session == null)
        {
            Count("unauthenticated");
            if (!internalRequest && binding.Mode == "page" && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
                context.Response.Redirect(Prefix + "/login?returnUrl=" + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
            else context.Response.StatusCode = 401;
            return;
        }
        var routePolicy = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.AuthorizationPolicy;
        if (string.IsNullOrEmpty(routePolicy)) routePolicy = null;
        var policyProvider = context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        var nativePolicy = routePolicy == null ? await policyProvider.GetFallbackPolicyAsync() :
            routePolicy.Equals("default", StringComparison.OrdinalIgnoreCase) ? await policyProvider.GetDefaultPolicyAsync() : await policyProvider.GetPolicyAsync(routePolicy);
        if (nativePolicy?.AuthenticationSchemes.Any(s => s != ExternalLoginCatalog.CookieScheme(site.Id)) == true)
        { context.Response.StatusCode = 403; return; }
        context.User = session.Ticket.Principal;
        // Explicit visitor metadata replaces framework fallback selection. Preserve the host's fallback requirements here.
        if (routePolicy == null && nativePolicy != null &&
            !(await context.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User, context, nativePolicy)).Succeeded)
        { context.Response.StatusCode = 403; return; }
        context.Features.Set(new Wafish.Plugins.GatewayVisitorIdentityFeature(site.Id));
        if (!SafeMethod(context.Request.Method) && !CsrfValid(context, site, session)) { Count("csrf_denied"); context.Response.StatusCode = 403; return; }
        string? accessToken = null;
        ExternalLoginApiTarget? target = null;
        if (binding.ApiTarget != null)
        {
            target = site.ApiTargets.Single(t => t.Id == binding.ApiTarget);
            var result = await AccessTokenAsync(session, site, context.RequestAborted);
            if (result.Status != 200) { context.Response.StatusCode = result.Status; return; }
            session = result.Session!;
            var provider = site.Providers.Single(p => p.Id == session.Ticket.Properties.Items[RedisVisitorSessionStore.ProviderKey]);
            var scopes = (session.Ticket.Properties.GetTokenValue("scope") ?? session.Ticket.Properties.Items.GetItem(".wafish-granted-scopes") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (target.RequiredScopes.Any(s => !scopes.Contains(s, StringComparer.Ordinal))) { context.Response.StatusCode = 403; return; }
            accessToken = session.Ticket.Properties.GetTokenValue("access_token");
        }
        context.Features.Set(new VisitorRequestFeature(site.Id, session.Key, target, accessToken));
        Count("authorized");
        await next(context);
    }

    private async Task<VisitorSession?> AuthenticateAsync(HttpContext context, ExternalLoginSite site)
    {
        var auth = await context.AuthenticateAsync(ExternalLoginCatalog.CookieScheme(site.Id));
        if (!auth.Succeeded || auth.Properties?.Items.GetItem(RedisVisitorSessionStore.SessionKey) is not { } key) return null;
        var session = await _sessions.ReadAsync(key);
        if (session?.Ticket.Properties.Items.GetItem(RedisVisitorSessionStore.SiteKey) != site.Id) return null;
        if (session.Ticket.Properties.Items.GetItem(".wafish-profile") != _catalog.Stamp(site.Id)) return null;
        if (!site.Providers.Any(p => p.Id == session.Ticket.Properties.Items.GetItem(RedisVisitorSessionStore.ProviderKey))) return null;
        return session;
    }
    internal async Task<(int Status, VisitorSession? Session)> AccessTokenAsync(VisitorSession original, ExternalLoginSite site, CancellationToken cancellation)
    {
        var session = original;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (session.RefreshOwner.Length > 0)
            {
                // The marker is never automatically released. A crashed refresher cannot replay a rotated token.
                if (!long.TryParse(session.RefreshOwner.Split(':')[0], out var started) || _clock.GetUtcNow().ToUnixTimeSeconds() - started > 30)
                { await _sessions.RemoveAsync(session.Key); return (401, null); }
                await Task.Delay(100, cancellation);
                session = await _sessions.ReadAsync(session.Key);
                if (session == null) return (401, null);
                continue;
            }
            var properties = session.Ticket.Properties;
            if (string.IsNullOrEmpty(properties.GetTokenValue("access_token"))) return (403, null);
            if (DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires) && expires > _clock.GetUtcNow().AddSeconds(60))
                return (200, session);
            var refresh = properties.GetTokenValue("refresh_token");
            if (string.IsNullOrEmpty(refresh)) { await _sessions.RemoveAsync(session.Key); return (401, null); }
            var owner = _clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + ":" + Guid.NewGuid().ToString("N");
            if (!await _sessions.BeginRefreshAsync(session, owner))
            { session = await _sessions.ReadAsync(session.Key); if (session == null) return (401, null); continue; }
            var provider = site.Providers.Single(p => p.Id == properties.Items[RedisVisitorSessionStore.ProviderKey]);
            var options = _oidc.Get(ExternalLoginCatalog.OidcScheme(site.Id, provider.Id));
            try
            {
                var metadata = await options.ConfigurationManager!.GetConfigurationAsync(cancellation);
                if (!Uri.TryCreate(metadata.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint) || tokenEndpoint.Scheme != "https") return (503, null);
                using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = provider.ClientId, ["client_secret"] = options.ClientSecret! })
                };
                using var client = _clients.CreateClient("Wafish.ExternalLogin.Tokens");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (!response.IsSuccessStatusCode) { await _sessions.RemoveAsync(session.Key); Count("refresh_failed"); return ((int)response.StatusCode == 400 ? 401 : 503, null); }
                await response.Content.LoadIntoBufferAsync(65536, cancellation);
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation));
                var root = json.RootElement;
                var token = root.GetProperty("access_token").GetString();
                if (string.IsNullOrEmpty(token) || !string.Equals(root.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) || !root.GetProperty("expires_in").TryGetInt32(out var seconds) || seconds <= 0)
                    throw new JsonException("Invalid token response.");
                var tokens = properties.GetTokens().ToDictionary(t => t.Name, t => t.Value);
                tokens["access_token"] = token; tokens["expires_at"] = _clock.GetUtcNow().AddSeconds(seconds).ToString("o", CultureInfo.InvariantCulture);
                foreach (var name in new[] { "refresh_token", "scope" }) if (root.TryGetProperty(name, out var value)) tokens[name] = value.GetString()!;
                properties.StoreTokens(tokens.Select(t => new AuthenticationToken { Name = t.Key, Value = t.Value }));
                if (!await _sessions.CompleteRefreshAsync(session, owner)) return (401, null);
                Count("refresh_succeeded"); return (200, session);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                // Keep the refresh marker on uncertain outcomes. Another request must never replay this refresh token.
                Count("refresh_unavailable");
                if (cancellation.IsCancellationRequested) throw;
                return (503, null);
            }
        }
        return (503, null);
    }
    internal static bool CsrfValid(HttpContext context, ExternalLoginSite site, VisitorSession session)
    {
        if (context.Request.Headers.Origin.Count != 1 || context.Request.Headers.Origin[0] != site.Origin) return false;
        var expected = session.Ticket.Properties.Items.GetItem(CsrfKey);
        var actual = context.Request.Headers[CsrfHeader];
        return expected is { Length: 64 } && actual.Count == 1 && actual[0]?.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual[0]!));
    }
    internal static bool SafeMethod(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
    internal static string Origin(HttpRequest request) => request.Scheme + "://" + request.Host.ToUriComponent();
    internal static bool LocalReturn(string? value) => value is { Length: > 0 and <= 2048 } && value[0] == '/' &&
        (value.Length == 1 || value[1] != '/') && !value.Contains('\\') && !value.Any(char.IsControl) &&
        !Uri.UnescapeDataString(value).Contains('\\') && !Uri.UnescapeDataString(value).StartsWith("//", StringComparison.Ordinal) &&
        !Uri.UnescapeDataString(value).Any(char.IsControl) && !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    internal static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() => { context.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
    }
    public void Dispose() { foreach (var limiter in _limits) limiter.Dispose(); }
    internal static void Count(string outcome) => Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}

internal sealed record VisitorRequestFeature(string Site, string Session, ExternalLoginApiTarget? Target, string? AccessToken);
