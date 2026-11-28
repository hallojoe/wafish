using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wafish.Plugin.ExternalLogin;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

internal static class ExternalLoginChecks
{
    private static void Check(bool condition, string message) => ClaimsTransformChecks.Check(condition, message);
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid external login configuration accepted."); }
    internal static ExternalLoginOptions Options(string directory) => new()
    {
        RedisConnectionReference = "Secrets:Redis", KeyRingPath = directory,
        Sites = [new() { Id = "website", Origin = "https://site.test", Providers = [new() { Id = "local", Authority = "https://identity.test/realms/test", ClientId = "gateway", ClientSecretReference = "Secrets:Client", Scopes = ["openid", "support:write"] }],
            ApiTargets = [new() { Id = "support", Audience = "support-api", Origins = ["https://support.test"], RequiredScopes = ["support:write"] }] }]
    };
    private static IConfiguration Configuration(ExternalLoginOptions options, string redis)
    {
        var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Gateway = new { ExternalLogin = options }, Secrets = new { Redis = redis, Client = "test-only-client-secret" } }));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("External login: startup settings, origins, bindings, and return URL validation", () =>
        {
            var options = Options(Path.GetTempPath());
            var catalog = new ExternalLoginCatalog(options);
            options.Sites[0].Id = "changed";
            Check(catalog.Site("website").Id == "website", "Catalog did not isolate host configuration.");
            foreach (var change in new Action<ExternalLoginOptions>[] { o => o.Sites[0].Origin = "http://site.test", o => o.Sites[0].Origin += "/path", o => o.Sites[0].Providers[0].Scopes = ["a b"], o => o.Sites[0].IdleMinutes = 0, o => o.Sites[0].ApiTargets[0].Origins = ["http://backend"], o => o.Sites = [o.Sites[0], o.Sites[0]], o => o.KeyRingPath = "relative" })
            { var invalid = Options(Path.GetTempPath()); change(invalid); Reject(() => new ExternalLoginCatalog(invalid)); }
            foreach (var path in new[] { "//evil.test", "/\\evil.test", "/%2fevil.test", "/%5cevil", "/%0d%0aLocation:evil", "https://evil.test", "/_wafish/auth/login" })
                Check(!ExternalLoginRuntime.LocalReturn(path), "Unsafe return URL accepted: " + path);
            Check(ExternalLoginRuntime.LocalReturn("/logged-in/account?tab=1"), "Local return rejected.");
            var binding = new PluginBindingSnapshot("binding", "external-login", Wafish.Storage.Entities.PluginBindingScope.Route, "route", "{\"site\":\"website\",\"mode\":\"page\"}", 1, 1, 0);
            var route = new RouteConfig { RouteId = "route", Match = new() { Hosts = ["site.test"], Path = "/logged-in/{**catch-all}" } };
            var compiler = new ExternalLoginCompiler(catalog);
            Check(compiler.Compile(binding, route).Route.Metadata!.ContainsKey(ExternalLoginCompiler.MetadataKey), "Binding metadata missing.");
            Reject(() => compiler.Compile(binding, route with { AuthorizationPolicy = "anonymous" }));
            Reject(() => compiler.Compile(binding, route with { Match = new() { Hosts = ["*.test"] } }));
            Reject(() => new ExternalLoginCompiler().Compile(binding, route));
            return Task.CompletedTask;
        });
        await run("External login: CSRF binds the request to its origin and server session", () =>
        {
            var site = Options(Path.GetTempPath()).Sites[0];
            var properties = new AuthenticationProperties(); properties.Items[".wafish-csrf"] = new string('A', 64);
            var session = new VisitorSession("session", new(new ClaimsPrincipal(new ClaimsIdentity([new("sub", "visitor")], "test")), properties, "test"), 0, "");
            var context = new DefaultHttpContext();
            Check(!ExternalLoginRuntime.CsrfValid(context, site, session), "Missing CSRF accepted.");
            context.Request.Headers.Origin = site.Origin; context.Request.Headers[ExternalLoginRuntime.CsrfHeader] = new string('A', 64);
            Check(ExternalLoginRuntime.CsrfValid(context, site, session), "Valid CSRF denied.");
            context.Request.Headers.Origin = "https://other.test";
            Check(!ExternalLoginRuntime.CsrfValid(context, site, session), "Cross-origin request accepted.");
            context.Request.Headers.Origin = site.Origin; context.Request.Headers[ExternalLoginRuntime.CsrfHeader] = new string('B', 64);
            Check(!ExternalLoginRuntime.CsrfValid(context, site, session), "Invalid CSRF accepted.");
            var sanitizer = new Wafish.Dashboard.Modules.Dashboard.Services.LogSanitizer(Microsoft.Extensions.Options.Options.Create(new Wafish.Plugin.ProxyLog.ProxyLogPluginOptions()));
            Check(sanitizer.SanitizeHeaders(context.Request.Headers)!.Single(h => h.Key == ExternalLoginRuntime.CsrfHeader).Value == "***REDACTED***", "CSRF header leaked into logs.");
            Check(!sanitizer.SanitizeJsonBody("{\"csrfToken\":\"sensitive-csrf\"}")!.Contains("sensitive-csrf"), "CSRF response field leaked into logs.");
            return Task.CompletedTask;
        });
        if (Environment.GetEnvironmentVariable("WAFISH_EXTERNALLOGIN_REDIS") is not { Length: > 0 } redis) return;
        await run("External login: public forwarding strips gateway cookies and rejects backend session replacement", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-cookie-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
                await using var backend = builder.Build();
                backend.MapGet("/public", (HttpContext context) =>
                {
                    context.Response.Headers.Append("Set-Cookie", "__Host-wafish-visitor-website=forged; Secure; Path=/");
                    context.Response.Headers.Append("Set-Cookie", "preferences=light; Path=/");
                    return Results.Text(context.Request.Headers.Cookie.ToString());
                });
                await backend.StartAsync();
                await using var fixture = await RedirectChecks.Fixture.Start(trustedForwarding: true, configure: services =>
                    services.AddWafishExternalLogin(Configuration(Options(directory), redis), new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = Environments.Development }));
                var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
                Check((await routes.TryAddClusterConfig(new() { ClusterId = "public", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = backend.Urls.Single() } } })).Success, "Public cluster failed.");
                Check((await routes.TryAddRouteConfig(new() { RouteId = "public", ClusterId = "public", Match = new() { Path = "/public" } })).Success, "Public route failed.");
                await fixture.WaitRoute("public");
                fixture.Client.DefaultRequestHeaders.Add("Cookie", "__Host-wafish-visitor-website=private-handle; preferences=dark");
                using var response = await fixture.Client.GetAsync("/public");
                Check(response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "preferences=dark", "Gateway cookie reached the public origin.");
                Check(response.Headers.GetValues("Set-Cookie").Single().StartsWith("preferences="), "Backend replaced the gateway session cookie.");
                await backend.StopAsync();
            }
            finally { Directory.Delete(directory, true); }
        });
        await run("External login: OIDC code callback validates state and nonce and stores only a session handle", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-oidc-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var signingKey = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "test-key" };
            var options = Options(directory); options.Namespace = "test-" + Guid.NewGuid().ToString("N");
            options.Sites[0].Providers = [options.Sites[0].Providers[0], new() { Id = "alternative", DisplayName = "<script>provider</script>", Authority = "https://other.test", ClientId = "other", ClientSecretReference = "Secrets:Client" }];
            string nonce = "", remoteFailure = "", visitorName = "Test visitor";
            try
            {
                await using var fixture = await RedirectChecks.Fixture.Start(trustedForwarding: true, configure: services =>
                {
                    services.AddWafishExternalLogin(Configuration(options, redis), new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = Environments.Development });
                    services.Configure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(ExternalLoginCatalog.OidcScheme("website", "local"), oidc =>
                    {
                        var metadata = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
                        { Issuer = "https://identity.test/realms/test", AuthorizationEndpoint = "https://identity.test/authorize", TokenEndpoint = "https://identity.test/token" };
                        metadata.SigningKeys.Add(signingKey);
                        oidc.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(metadata);
                        var failureHandler = oidc.Events.OnRemoteFailure;
                        oidc.Events.OnRemoteFailure = async context => { remoteFailure = context.Failure?.Message ?? ""; await failureHandler(context); };
                        oidc.Backchannel = new HttpClient(new OidcTokenHandler(() =>
                        {
                            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(metadata.Issuer, "gateway", [new("sub", visitorName), new("nonce", nonce), new("name", visitorName), new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)], DateTime.UtcNow.AddSeconds(-10), DateTime.UtcNow.AddMinutes(5), new(signingKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));
                            return JsonSerializer.Serialize(new { access_token = "test-access-token", refresh_token = "test-refresh-token", id_token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(jwt), token_type = "Bearer", expires_in = 300, scope = "openid support:write" });
                        }));
                    });
                });
                fixture.Client.DefaultRequestHeaders.Host = "site.test";
                fixture.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
                using var selector = await fixture.Client.GetAsync("/_wafish/auth/login");
                var selectorHtml = await selector.Content.ReadAsStringAsync();
                Check(selectorHtml.Contains("&lt;script&gt;") && !selectorHtml.Contains("<script>"), "Provider labels were not encoded.");
                using var unknown = await fixture.Client.GetAsync("/_wafish/auth/login/unknown");
                Check(unknown.StatusCode == HttpStatusCode.NotFound, "Unknown provider accepted.");
                using var unsafeReturn = await fixture.Client.GetAsync("/_wafish/auth/login?returnUrl=https%3A%2F%2Fevil.test");
                Check(unsafeReturn.StatusCode == HttpStatusCode.BadRequest, "External return URL accepted.");
                string? sessionCookie = null;
                foreach (var valid in new[] { false, true, true })
                {
                    if (sessionCookie != null) visitorName = "Replacement visitor";
                    using var challenge = await fixture.Client.GetAsync("/_wafish/auth/login/local?returnUrl=%2Flogged-in");
                    Check(challenge.StatusCode == HttpStatusCode.Redirect, "OIDC challenge failed.");
                    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
                    Check(query["code_challenge_method"] == "S256", "PKCE missing.");
                    nonce = valid ? query["nonce"].ToString() : "invalid-nonce";
                    var cookies = string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
                    if (sessionCookie != null) cookies += "; " + sessionCookie;
                    using var callback = new HttpRequestMessage(HttpMethod.Post, ExternalLoginCatalog.Callback("website", "local"))
                    { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = query["state"].ToString(), ["code"] = "test-code" }) };
                    callback.Headers.Add("Cookie", cookies);
                    using var response = await fixture.Client.SendAsync(callback);
                    Check(response.StatusCode == (valid ? HttpStatusCode.Redirect : HttpStatusCode.BadRequest), "OIDC nonce validation or callback failed: " + valid + " " + response.StatusCode + " " + remoteFailure);
                    if (!valid) continue;
                    Check(response.Headers.Location!.OriginalString == "/logged-in", "OIDC return URL changed.");
                    var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-wafish-visitor"));
                    Check(!cookie.Contains("test-access-token") && !cookie.Contains("test-refresh-token"), "Token leaked into cookie.");
                    sessionCookie = cookie.Split(';')[0];
                    fixture.Client.DefaultRequestHeaders.Remove("Cookie");
                    fixture.Client.DefaultRequestHeaders.Add("Cookie", sessionCookie);
                    using var status = await fixture.Client.GetAsync("/_wafish/auth/session");
                    var sessionJson = await status.Content.ReadAsStringAsync();
                    Check(sessionJson.Contains("\"authenticated\":true") && sessionJson.Contains(visitorName), "OIDC login did not persist the current identity.");
                }
                using var badState = await fixture.Client.PostAsync(ExternalLoginCatalog.Callback("website", "local"), new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = "bad", ["code"] = "bad" }));
                Check(badState.StatusCode == HttpStatusCode.BadRequest, "Invalid OIDC state accepted.");
            }
            finally { Directory.Delete(directory, true); }
        });
        await run("External login: token renewal, denied grants, concurrent refresh, and provider failures", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-token-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var options = Options(directory); options.Namespace = "test-" + Guid.NewGuid().ToString("N");
                var catalog = new ExternalLoginCatalog(options);
                using var store = new RedisVisitorSessionStore(redis, catalog, DataProtectionProvider.Create(new DirectoryInfo(directory)), TimeProvider.System);
                var http = new TokenClient();
                var oidc = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions
                {
                    ClientSecret = "secret",
                    ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(new() { TokenEndpoint = "https://identity.test/token" })
                };
                var runtime = new ExternalLoginRuntime(catalog, store, new FixedOptions(oidc), http, TimeProvider.System);
                async Task<VisitorSession> Session(bool token = true, bool refresh = true, int seconds = -1)
                {
                    var ticket = Ticket();
                    var tokens = new List<AuthenticationToken>();
                    if (token) tokens.Add(new() { Name = "access_token", Value = "old-access" });
                    if (refresh) tokens.Add(new() { Name = "refresh_token", Value = "old-refresh" });
                    tokens.Add(new() { Name = "expires_at", Value = DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("o") });
                    ticket.Properties.StoreTokens(tokens);
                    return (await store.ReadAsync(await store.StoreAsync(ticket)))!;
                }
                http.Response = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"token_type\":\"Bearer\",\"expires_in\":300,\"scope\":\"support:write\"}") });
                var valid = await Session(seconds: 300);
                Check((await runtime.AccessTokenAsync(valid, catalog.Site("website"), default)).Status == 200 && http.Calls == 0, "Valid token refreshed unnecessarily.");
                Check((await runtime.AccessTokenAsync(await Session(token: false), catalog.Site("website"), default)).Status == 403, "Missing access token accepted.");
                var noRefresh = await Session(refresh: false);
                Check((await runtime.AccessTokenAsync(noRefresh, catalog.Site("website"), default)).Status == 401 && await store.ReadAsync(noRefresh.Key) == null, "Expired credentials retained.");
                var expired = await Session();
                var renewed = await runtime.AccessTokenAsync(expired, catalog.Site("website"), default);
                Check(renewed.Status == 200 && renewed.Session!.Ticket.Properties.GetTokenValue("access_token") == "new-access", "Access token did not rotate.");
                Check((await store.ReadAsync(expired.Key))!.Ticket.Properties.GetTokenValue("refresh_token") == "new-refresh", "Refresh token rotation was not persisted.");
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var response = http.Response;
                http.Response = async () => { start.TrySetResult(); await release.Task; return await response(); };
                var concurrent = await Session(); var calls = http.Calls;
                var first = runtime.AccessTokenAsync(concurrent, catalog.Site("website"), default);
                await start.Task;
                var second = runtime.AccessTokenAsync(concurrent, catalog.Site("website"), default);
                release.SetResult();
                var results = await Task.WhenAll(first, second);
                Check(results.All(r => r.Status == 200) && http.Calls == calls + 1, "Concurrent requests reused a refresh token.");
                foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.ServiceUnavailable })
                {
                    http.Response = () => Task.FromResult(new HttpResponseMessage(status));
                    var failed = await Session();
                    Check((await runtime.AccessTokenAsync(failed, catalog.Site("website"), default)).Status == (status == HttpStatusCode.BadRequest ? 401 : 503), "Provider failure status incorrect.");
                    Check(await store.ReadAsync(failed.Key) == null, "Failed refresh left reusable credentials.");
                }
                http.Response = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                var malformed = await Session();
                Check((await runtime.AccessTokenAsync(malformed, catalog.Site("website"), default)).Status == 503, "Malformed token accepted.");
                Check((await store.ReadAsync(malformed.Key))!.RefreshOwner.Length > 0, "Uncertain refresh became reusable.");
                var abandoned = await Session();
                await store.BeginRefreshAsync(abandoned, "0:crashed");
                Check((await runtime.AccessTokenAsync((await store.ReadAsync(abandoned.Key))!, catalog.Site("website"), default)).Status == 401, "Abandoned refresh did not expire.");
                await store.RemoveAsync(valid.Key); await store.RemoveAsync(expired.Key); await store.RemoveAsync(concurrent.Key); await store.RemoveAsync(malformed.Key);
            }
            finally { Directory.Delete(directory, true); }
        });
        await run("External login: Redis refresh fencing, encryption, logout, and replica continuity", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-visitor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var options = Options(directory); options.Namespace = "test-" + Guid.NewGuid().ToString("N");
                var catalog = new ExternalLoginCatalog(options);
                var protection = DataProtectionProvider.Create(new DirectoryInfo(directory));
                using var first = new RedisVisitorSessionStore(redis, catalog, protection, TimeProvider.System);
                using var second = new RedisVisitorSessionStore(redis, catalog, protection, TimeProvider.System);
                var ticket = Ticket(); var key = await first.StoreAsync(ticket);
                var snapshot = (await second.ReadAsync(key))!;
                Check(snapshot.Ticket.Principal.FindFirst("sub")!.Value == "visitor", "Replica lost the session.");
                var winners = await Task.WhenAll(first.BeginRefreshAsync(snapshot, "first"), second.BeginRefreshAsync(snapshot, "second"));
                Check(winners.Count(w => w) == 1, "Concurrent refresh acquired multiple owners.");
                var owner = winners[0] ? "first" : "second";
                Check(!await first.CompleteRefreshAsync(snapshot, "other"), "Invalid refresh owner succeeded.");
                Check(await first.CompleteRefreshAsync(snapshot, owner), "Refresh could not commit.");
                Check(!await first.CompleteRefreshAsync(snapshot, owner), "Stale refresh committed twice.");
                var current = (await second.ReadAsync(key))!;
                Check(current.Version == 1 && current.RefreshOwner.Length == 0, "Refresh version or marker incorrect.");
                Check(await first.BeginRefreshAsync(current, "relogin-race"), "Refresh lock unavailable before login.");
                var replacement = Ticket(); replacement.Properties.Items["login-marker"] = "replacement";
                await second.RenewAsync(key, replacement);
                Check(!await first.CompleteRefreshAsync(current, "relogin-race"), "Stale refresh overwrote a new login.");
                current = (await first.ReadAsync(key))!;
                Check(current.Version == 2 && current.Ticket.Properties.Items["login-marker"] == "replacement", "New login was not persisted.");
                Check(await first.BeginRefreshAsync(current, "logout-race"), "Refresh lock unavailable.");
                await second.RemoveAsync(key);
                Check(!await first.CompleteRefreshAsync(current, "logout-race"), "Refresh restored a logged-out session.");
                Check(await first.RetrieveAsync(key) == null, "Deleted session survived.");
                try { await second.RenewAsync(key, replacement); throw new Exception("Renewal restored a logged-out session."); }
                catch (InvalidOperationException) { }
            }
            finally { Directory.Delete(directory, true); }
        });
        await run("External login: protected pages, API challenges, CSRF, and administration isolation", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-visitor-host-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var options = Options(directory); options.Namespace = "test-" + Guid.NewGuid().ToString("N");
            try
            {
                await using var fixture = await RedirectChecks.Fixture.Start(trustedForwarding: true, configure: services =>
                {
                    services.AddWafishExternalLogin(Configuration(options, redis), new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = Environments.Development });
                    services.AddAuthorization(o => o.AddPolicy("visitor-denied", p => p.RequireClaim("required-permission")));
                });
                fixture.Client.DefaultRequestHeaders.Host = "site.test";
                fixture.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
                var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
                foreach (var mode in new[] { "page", "api" })
                {
                    Check((await routes.TryAddRouteConfig(new() { RouteId = mode, Match = new() { Hosts = ["site.test"], Path = "/" + mode }, AuthorizationPolicy = mode == "api" ? "" : null })).Success, "Route creation failed.");
                    await fixture.Bind(mode, "redirect", "{\"target\":\"/allowed\"}");
                    await fixture.Bind(mode, "external-login", "{\"site\":\"website\",\"mode\":\"" + mode + "\"}");
                    await fixture.WaitRoute(mode);
                }
                foreach (var id in new[] { "native-denied", "claims-denied" })
                {
                    Check((await routes.TryAddRouteConfig(new() { RouteId = id, Match = new() { Hosts = ["site.test"], Path = "/" + id }, AuthorizationPolicy = id == "native-denied" ? "visitor-denied" : null })).Success, "Denied route creation failed.");
                    await fixture.Bind(id, "redirect", "{\"target\":\"/allowed\"}");
                    await fixture.Bind(id, "external-login", "{\"site\":\"website\",\"mode\":\"api\"}");
                    if (id == "claims-denied") await fixture.Bind(id, "route-authorization", "{\"allowedScopes\":[\"missing\"]}");
                    await fixture.WaitRoute(id);
                }
                using var page = await fixture.Client.GetAsync("/page");
                Check(page.StatusCode == HttpStatusCode.Redirect && page.Headers.Location!.OriginalString.StartsWith("/_wafish/auth/login"), "Page did not challenge.");
                using var api = await fixture.Client.GetAsync("/api");
                Check(api.StatusCode == HttpStatusCode.Unauthorized && api.Headers.Location == null, "API redirected or accepted administration credentials.");
                await using var requestScope = fixture.App.Services.CreateAsyncScope();
                var context = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
                context.Request.Scheme = "https"; context.Request.Host = new("site.test");
                var ticket = Ticket();
                await context.SignInAsync(ExternalLoginCatalog.CookieScheme("website"), ticket.Principal, ticket.Properties);
                var cookie = context.Response.Headers.SetCookie.Single()!.Split(';')[0];
                fixture.Client.DefaultRequestHeaders.Add("Cookie", cookie);
                await using (var replica = await RedirectChecks.Fixture.Start(trustedForwarding: true, configure: services =>
                    services.AddWafishExternalLogin(Configuration(options, redis), new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = Environments.Development })))
                {
                    replica.Client.DefaultRequestHeaders.Host = "site.test";
                    replica.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
                    replica.Client.DefaultRequestHeaders.Add("Cookie", cookie);
                    using var shared = await replica.Client.GetAsync("/_wafish/auth/session");
                    Check((await shared.Content.ReadAsStringAsync()).Contains("\"authenticated\":true"), "Second gateway rejected the session cookie.");
                    replica.Client.DefaultRequestHeaders.Host = "other.test";
                    using var crossSite = await replica.Client.GetAsync("/_wafish/auth/session");
                    Check(crossSite.StatusCode == HttpStatusCode.NotFound, "Another site accepted the session.");
                }
                foreach (var id in new[] { "native-denied", "claims-denied" })
                {
                    using var denied = await fixture.Client.GetAsync("/" + id);
                    Check(denied.StatusCode == HttpStatusCode.Forbidden && denied.Headers.Location == null, "Visitor policy denial redirected or failed.");
                }
                using var allowed = await fixture.Client.GetAsync("/page");
                Check((int)allowed.StatusCode == 307 && allowed.Headers.Location!.OriginalString == "/allowed", "Authenticated page denied.");
                var authorization = fixture.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Authorization.AuthorizationOptions>>().Value;
                authorization.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireClaim("missing-fallback-claim").Build();
                using var fallback = await fixture.Client.GetAsync("/page");
                Check(fallback.StatusCode == HttpStatusCode.Forbidden, "Visitor metadata bypassed the host fallback policy.");
                using var emptyPolicy = await fixture.Client.GetAsync("/api");
                Check(emptyPolicy.StatusCode == HttpStatusCode.Forbidden, "Empty route policy bypassed the host fallback policy.");
                authorization.FallbackPolicy = null;
                using var csrf = await fixture.Client.PostAsync("/api", new StringContent("ticket"));
                Check(csrf.StatusCode == HttpStatusCode.Forbidden, "Missing CSRF accepted.");
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api") { Content = new StringContent("ticket") };
                request.Headers.Add("Origin", "https://site.test"); request.Headers.Add(ExternalLoginRuntime.CsrfHeader, new string('A', 64));
                using var submitted = await fixture.Client.SendAsync(request);
                Check((int)submitted.StatusCode == 307, "Valid CSRF denied.");
                using var status = await fixture.Client.GetAsync("/_wafish/auth/session");
                var body = await status.Content.ReadAsStringAsync(); Check(body.Contains("\"authenticated\":true") && !body.Contains("access_token"), "Session endpoint leaked tokens or lost identity.");
                using var logout = new HttpRequestMessage(HttpMethod.Post, "/_wafish/auth/logout");
                logout.Headers.Add("Origin", "https://site.test"); logout.Headers.Add(ExternalLoginRuntime.CsrfHeader, new string('A', 64));
                using var signedOut = await fixture.Client.SendAsync(logout); Check(signedOut.StatusCode == HttpStatusCode.NoContent, "Logout failed.");
                using var expired = await fixture.Client.GetAsync("/api"); Check(expired.StatusCode == HttpStatusCode.Unauthorized, "Logout left an active session.");
            }
            finally { Directory.Delete(directory, true); }
        });
    }
    internal static AuthenticationTicket Ticket()
    {
        var properties = new AuthenticationProperties();
        properties.Items[RedisVisitorSessionStore.SiteKey] = "website"; properties.Items[RedisVisitorSessionStore.ProviderKey] = "local";
        properties.Items[".wafish-csrf"] = new string('A', 64);
        properties.Items[".wafish-profile"] = ExternalLoginCatalog.ProfileStamp(Options(Path.GetTempPath()).Sites[0]);
        return new(new ClaimsPrincipal(new ClaimsIdentity([new("sub", "visitor"), new("name", "Test visitor")], ExternalLoginCatalog.CookieScheme("website"))), properties, ExternalLoginCatalog.CookieScheme("website"));
    }
    private sealed class FixedOptions(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions options) : Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>
    {
        public Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions CurrentValue => options;
        public Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions, string?> listener) => null;
    }
    private sealed class TokenClient : HttpMessageHandler, IHttpClientFactory
    {
        internal Func<Task<HttpResponseMessage>> Response = () => throw new InvalidOperationException("Unexpected token request.");
        internal int Calls;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Check(request.RequestUri == new Uri("https://identity.test/token"), "Token request used an untrusted endpoint.");
            Check((await request.Content!.ReadAsStringAsync(cancellationToken)).Contains("grant_type=refresh_token"), "Wrong grant type.");
            return await Response();
        }
    }
    private sealed class OidcTokenHandler(Func<string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(), System.Text.Encoding.UTF8, "application/json") });
    }
}
