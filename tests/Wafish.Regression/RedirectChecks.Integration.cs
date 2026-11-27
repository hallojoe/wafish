using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Infrastructure.State;
using Wafish.Plugin.RateLimit.Redis;
using Wafish.Plugin.Metrics;
using Wafish.Plugin.Redirect;
using Wafish.Services;
using Wafish.Plugins;
using Wafish.Storage;
using Yarp.ReverseProxy.Configuration;

internal static partial class RedirectChecks
{
    private static async Task RunIntegrationAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Redirect: rename, restart, rollback, and disabled routes retain bindings", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-redirect-restart-" + Guid.NewGuid().ToString("N"));
            try
            {
                string uid;
                await using (var fixture = await Fixture.Start(directory: directory))
                {
                    var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
                    Check((await config.TryAddRouteConfig(Route("/persist/{item}"))).Success, "Could not save redirect route.");
                    await fixture.WaitRoute("r");
                    var binding = await fixture.Bind("r", "redirect", "{\"target\":\"/saved/{route.item}\"}");
                    uid = binding.RouteUid!;
                    Check((await config.TryRenameRoute("r", "renamed", new Wafish.Models.RegisterRouteRequest())).Success, "Clusterless rename failed.");
                    await fixture.WaitRoute("renamed");
                    using var response = await fixture.Client.GetAsync("/persist/one");
                    Check(response.Headers.Location?.OriginalString == "/saved/one", "Renamed binding stopped executing.");
                }
                await using (var fixture = await Fixture.Start(directory: directory))
                {
                    await fixture.WaitRoute("renamed");
                    var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
                    var route = config.GetRoutes().Single();
                    Check(string.IsNullOrEmpty(route.ClusterId) && config.GetClusters().Count == 0, "Restart invented a cluster.");
                    var bindings = await fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>().GetBindingsAsync();
                    Check(bindings.Single().RouteUid == uid, "Rename or restart replaced binding identity.");
                    using (var response = await fixture.Client.GetAsync("/persist/two"))
                        Check(response.Headers.Location?.OriginalString == "/saved/two", "Restart lost redirect behavior.");
                    Check((await config.TryAddRouteConfig(route with { Match = new() { Path = "/changed/{item}" } })).Success, "Route replacement failed.");
                    await config.ReplaceAllConfig([route], []);
                    // Force endpoint reload observation before issuing the restored request.
                    var endpoints = fixture.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (!endpoints.Endpoints.Any(e => e.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config.Match.Path == "/persist/{item}"))
                        await Task.Delay(10, timeout.Token);
                    using (var restored = await fixture.Client.GetAsync("/persist/three"))
                        Check(restored.Headers.Location?.OriginalString == "/saved/three", "Rollback lost redirect binding.");
                    Check((await config.TrySetRouteEnabled("renamed", false)).Success, "Route disable failed.");
                    while (endpoints.Endpoints.Any(e => e.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config.RouteId == "renamed"))
                        await Task.Delay(10, timeout.Token);
                    using var disabled = await fixture.Client.GetAsync("/persist/one");
                    Check(disabled.StatusCode == HttpStatusCode.NotFound, "Disabled route remained active.");
                }
            }
            finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
        });
        await run("Redirect: unavailable backends, HTTPS pass-through, and forwarding fallback", async () =>
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var backend = builder.Build();
            var calls = 0;
            backend.Map("/{**path}", context => { Interlocked.Increment(ref calls); return context.Response.WriteAsync("backend"); });
            await backend.StartAsync();
            await using var fixture = await Fixture.Start(trustedForwarding: true);
            var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            var route = Route("/move") with { ClusterId = "backend" };
            Check((await config.ImportBatchAsync([new ClusterConfig { ClusterId = "backend", Destinations = new Dictionary<string, DestinationConfig>
                { ["one"] = new() { Address = backend.Urls.Single() } } }], [route])).Success, "Could not create backend route.");
            await fixture.WaitRoute(route.RouteId);
            var redirect = await fixture.Bind(route.RouteId, "redirect", "{\"target\":\"/new\"}");
            var maintenance = fixture.App.Services.GetRequiredService<IDestinationMaintenanceStore>();
            maintenance.Set("backend", "one", "test", "isolated", TimeSpan.FromMinutes(1), false);
            using (var response = await fixture.Client.PostAsync("/move", new StringContent("body")))
                Check((int)response.StatusCode == 307 && calls == 0, "Redirect reached an unavailable backend.");
            maintenance.Remove("backend", "one");
            var mutations = fixture.App.Services.GetRequiredService<IPluginBindingMutationService>();
            redirect.Enabled = false; await mutations.UpsertAsync(redirect);
            using (var response = await fixture.Client.GetAsync("/move"))
                Check(response.StatusCode == HttpStatusCode.OK && calls == 1, "Disabled binding did not resume forwarding.");
            redirect.Enabled = true;
            redirect.ConfigJson = "{\"mode\":\"HttpsOnly\",\"allowedHosts\":[\"127.0.0.1\"]}";
            await mutations.UpsertAsync(redirect);
            await fixture.Bind(route.RouteId, "traffic-metrics", "{\"enabled\":true}");
            await fixture.Bind(route.RouteId, "rate-limit", "{\"enabled\":true,\"permitLimit\":2,\"partitionKey\":\"Route\"}");
            using (var response = await fixture.Client.GetAsync("/move?token=hidden"))
                Check((int)response.StatusCode == 307 && response.Headers.Location?.OriginalString == "https://127.0.0.1/move", "HTTPS redirect failed.");
            using var secure = new HttpRequestMessage(HttpMethod.Get, "/move"); secure.Headers.Add("X-Forwarded-Proto", "https");
            using (var response = await fixture.Client.SendAsync(secure))
                Check(response.StatusCode == HttpStatusCode.OK && calls == 2, "HTTPS pass-through ran policies twice.");
            Check(fixture.App.Services.GetRequiredService<PluginMetricStore>().GetRoutes()[route.RouteId].Requests == 2, "HTTPS pass-through duplicated metrics.");
            var request = Request("http://example.com/old"); request.Headers["X-Forwarded-Proto"] = "https";
            Check(Evaluate(new() { Mode = RedirectMode.HttpsOnly, AllowedHosts = ["example.com"] }, request).Outcome == "Redirect", "Raw forwarded headers bypassed HTTPS enforcement.");
        });
        await run("Redirect: WAF and route authorization reject before redirect evaluation", async () =>
        {
            await using var fixture = await Fixture.Start(services =>
            {
                services.AddAuthentication("redirect-test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("redirect-test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("deny-redirect", policy => policy.RequireAssertion(_ => false)));
            });
            var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await config.TryAddRouteConfig(Route("/blocked") with { RouteId = "blocked" })).Success, "WAF route failed.");
            await fixture.WaitRoute("blocked"); await fixture.Bind("blocked", "redirect", "{\"target\":\"/new\"}");
            await fixture.Bind("blocked", "waf", "{\"enabled\":true,\"ipBlacklist\":[\"127.0.0.1\",\"::1\"]}");
            using (var response = await fixture.Client.GetAsync("/blocked"))
                Check(response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Location is null, "Redirect bypassed WAF.");
            Check((await config.TryAddRouteConfig(Route("/protected") with { RouteId = "protected", AuthorizationPolicy = "deny-redirect" })).Success, "Protected route failed.");
            await fixture.WaitRoute("protected"); await fixture.Bind("protected", "redirect", "{\"target\":\"/new\"}");
            using var protectedResponse = await fixture.Client.GetAsync("/protected");
            Check(protectedResponse.StatusCode == HttpStatusCode.Forbidden && protectedResponse.Headers.Location is null, "Redirect bypassed route authorization.");
        });
        await run("Redirect: Redis policy, in-flight replacement, cancellation, and disabled plugin", async () =>
        {
            var redis = new ControlledRedis();
            await using var fixture = await Fixture.Start(services =>
            {
                var registration = services.Single(s => s.ServiceType == typeof(IGatewayPlugin) && s.ImplementationType == typeof(RedisRateLimitPlugin));
                services.Remove(registration);
                services.AddSingleton<IGatewayPlugin>(new TestRedisPlugin(redis));
            });
            var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await config.TryAddRouteConfig(Route("/hold"))).Success, "Route failed."); await fixture.WaitRoute("r");
            var redirect = await fixture.Bind("r", "redirect", "{\"target\":\"/before\"}");
            await fixture.Bind("r", "rate-limit-redis", "{\"enabled\":true}");
            redis.Block = true;
            var pending = fixture.Client.GetAsync("/hold");
            await redis.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                redirect.ConfigJson = "{\"target\":\"/after\"}";
                await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(redirect);
            }
            finally { redis.Release.TrySetResult(); }
            using (var response = await pending) Check(response.Headers.Location?.OriginalString == "/before", "In-flight template changed during replacement.");
            redis.Block = false;
            using (var response = await fixture.Client.GetAsync("/hold")) Check(response.Headers.Location?.OriginalString == "/after", "Replacement did not activate.");
            redis.Allowed = false;
            using (var response = await fixture.Client.GetAsync("/hold")) Check((int)response.StatusCode == 429 && response.Headers.Location is null, "Redis rejection was bypassed.");
            redis.Available = false;
            using (var response = await fixture.Client.GetAsync("/hold")) Check((int)response.StatusCode == 503, "Redis outage did not fail closed.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var request = Request("http://example.com/hold"); request.HttpContext.RequestAborted = cancellation.Token;
            try { Evaluate(new() { Target = "/new" }, request); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
            var manager = fixture.App.Services.GetRequiredService<PluginRuntimeDomainManager>();
            var enabled = manager.Current.Plugins.Keys.Where(id => id != "redirect").ToArray();
            Check((await manager.TransitionAsync(enabled)).Succeeded, "Plugin disable failed.");
            using var disabled = await fixture.Client.GetAsync("/hold");
            Check((int)disabled.StatusCode == 503, "Disabled plugin executed its binding.");
        });
        await run("Redirect: preview route competition, draft validation, and route API without clusters", async () =>
        {
            await using var fixture = await Fixture.Start();
            using var saved = await fixture.Client.PutAsJsonAsync("/ops/api/config/routes/general", new { Match = new { Path = "/items/{id}" } });
            Check(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync()); await fixture.WaitRoute("general");
            var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await config.TryAddRouteConfig(Route("/items/special") with { RouteId = "specific", Order = 0 })).Success, "Competing route failed.");
            await fixture.WaitRoute("specific");
            var endpointCount = fixture.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.Count;
            foreach (var example in new[] { ("/items/one", "Redirect"), ("/items/special", "RouteMismatch"), ("/elsewhere", "RouteMismatch") })
            {
                using var result = await fixture.Client.PostAsJsonAsync("/ops/api/plugins/redirect/preview", new
                {
                    binding = new { pluginId = "redirect", scope = "Route", scopeId = "general", configJson = "{\"target\":\"/new/{route.id}\"}" },
                    request = new { url = "http://example.com" + example.Item1 }
                });
                var text = await result.Content.ReadAsStringAsync();
                Check(result.IsSuccessStatusCode && text.Contains(example.Item2), "Preview matching failed for " + example.Item1 + ": " + text);
            }
            Check(fixture.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.Count == endpointCount,
                "Preview changed the host endpoint inventory.");
            foreach (var invalidConfig in new[] { "{\"target\":\"/new\",\"mode\":0}", "{\"target\":\"/new\",\"unknown\":true}" })
            {
                using var invalid = await fixture.Client.PostAsJsonAsync("/ops/api/plugins/redirect/preview", new
                {
                    binding = new { pluginId = "redirect", scope = "Route", scopeId = "general", configJson = invalidConfig },
                    request = new { url = "http://example.com/items/one" }
                });
                Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Preview bypassed the binding schema.");
            }
            foreach (var sample in new object[]
            {
                new { url = "ftp://example.com/items/one" },
                new { url = "http://example.com/items/one", pathBase = "/wrong" },
                new { url = "http://example.com/items/one", headers = new Dictionary<string, string> { ["Host"] = "other.example" } }
            })
            {
                using var invalid = await fixture.Client.PostAsJsonAsync("/ops/api/plugins/redirect/preview", new
                {
                    binding = new { pluginId = "redirect", scope = "Route", scopeId = "general", configJson = "{\"target\":\"/new\"}" }, request = sample
                });
                Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid preview input was accepted.");
            }
            using (var based = await fixture.Client.PostAsJsonAsync("/ops/api/plugins/redirect/preview", new
            {
                binding = new { pluginId = "redirect", scope = "Route", scopeId = "general", configJson = "{\"target\":\"https://target.example{request.path}\"}" },
                request = new { url = "http://example.com/edge/items/one", pathBase = "/edge", headers = new Dictionary<string, string> { ["X-Sample"] = "value" } }
            }))
                Check(based.IsSuccessStatusCode && (await based.Content.ReadAsStringAsync()).Contains("https://target.example/edge/items/one"), "Path-base preview failed.");
            var mutation = fixture.App.Services.GetRequiredService<IPluginBindingMutationService>();
            var binding = await fixture.Bind("general", "redirect", "{\"target\":\"/new/{route.id}\"}");
            binding.Enabled = false; binding.ConfigJson = "{\"target\":\"javascript:bad\"}";
            try { await mutation.UpsertAsync(binding); throw new Exception("Invalid disabled binding saved."); } catch (ArgumentException) { }
            using var unknownCluster = await fixture.Client.PutAsJsonAsync("/ops/api/config/routes/bad", new { ClusterId = "missing", Match = new { Path = "/bad" } });
            Check(!unknownCluster.IsSuccessStatusCode, "Missing referenced cluster was accepted.");
        });
    }

    private sealed class TestRedisPlugin(ControlledRedis store) : IGatewayPlugin
    {
        private readonly RedisRateLimitPlugin _inner = new();
        public string PluginId => _inner.PluginId;
        public string DisplayName => _inner.DisplayName;
        public string Version => _inner.Version;
        public PluginManifest Manifest => _inner.Manifest;
        public void ConfigureServices(IServiceCollection services, object? options = null) => services.AddSingleton<IDistributedRateLimitStore>(store);
        public void ConfigureMiddleware(IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder pipeline) => _inner.ConfigureProxyPipeline(pipeline);
    }
    private sealed class ControlledRedis : IDistributedRateLimitStore
    {
        public bool Block, Allowed = true, Available = true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit, int windowSeconds,
            int burstBalance, string? redisConnectionString, CancellationToken cancellationToken = default)
        {
            if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return Available ? new(Allowed, limit, 0, 1) : DistributedRateLimitResult.Unavailable(limit);
        }
    }
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], Scheme.Name)), Scheme.Name)));
    }
}
