using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wafish.Administration;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugin.Redirect;
using Wafish.Plugin.Metrics;
using Wafish.Plugin.ProxyLog.Services;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Infrastructure.Performance;
using Wafish.Runtime;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Wafish.Storage.Sqlite;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

internal static partial class RedirectChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await RunIntegrationAsync(run);
        await run("Redirect: status codes, templates, queries, and HTTPS semantics", () =>
        {
            foreach (var status in new[] { 301, 302, 303, 307, 308 })
            foreach (var method in new[] { "GET", "HEAD", "POST" })
            {
                var request = Request("https://source.example/old/one?secret=yes", method);
                request.RouteValues["item"] = "one";
                var result = Evaluate(new() { Target = "/new/{route.item}", StatusCode = status }, request, "/old/{item}");
                Check(result.StatusCode == status && result.Location == "/new/one", "Status or query default changed.");
            }
            var value = Request("http://source.example/old?x=1&x=2&fixed=incoming&Fixed=case");
            var merged = Evaluate(new() { Target = "/new?fixed=target#section", QueryMode = RedirectQueryMode.Preserve }, value);
            Check(merged.Location == "/new?fixed=target&x=1&x=2&Fixed=case#section", "Query precedence or duplicates changed.");
            var path = Request("http://source.example/edge/old/a%20b/c");
            path.PathBase = "/edge"; path.Path = "/old/a b/c"; path.RouteValues["rest"] = "a b/c";
            Check(Evaluate(new() { Target = "/new/{**route.rest}" }, path, "/old/{**rest}").Location == "/new/a%20b/c", "Catch-all encoding failed.");
            Check(Evaluate(new() { Target = "/new/{**route.rest}" }, Request("http://example.com/old"), "/old/{**rest}").Location == "/new/", "Empty catch-all failed.");
            Check(Evaluate(new() { Target = "/café" }, path).Location == "/caf%C3%A9", "Literal Unicode was not encoded.");
            Check(Evaluate(new() { Target = "/old/a%2Fb" }, Request("http://example.com/old/a%2fb")).StatusCode == 400, "Escape casing bypassed the self-loop check.");
            Check(Evaluate(new() { Target = "https://target.example{request.path}" }, path).Location == "https://target.example/edge/old/a%20b/c", "PathBase was lost.");
            var https = new RedirectOptions { Mode = RedirectMode.HttpsOnly, AllowedHosts = ["source.example"] };
            Check(Evaluate(https, value).Location == "https://source.example/old", "HTTPS upgrade failed.");
            Check(Evaluate(https with { HttpsPort = 8443 }, value).Location == "https://source.example:8443/old", "HTTPS port failed.");
            value.Scheme = "https";
            Check(Evaluate(https, value).Outcome == "PassThrough", "Secure request did not continue.");
            return Task.CompletedTask;
        });
        await run("Redirect: exact hosts, unsafe targets, tokens, and self-loop checks", () =>
        {
            var request = Request("http://source.example/old"); request.RouteValues["tenant"] = "shop";
            var options = new RedirectOptions { Target = "https://{route.tenant}.example/new", AllowedHosts = ["SHOP.EXAMPLE."] };
            Check(Evaluate(options, request, "/{tenant}").Location == "https://shop.example/new", "Exact host normalization failed.");
            request.RouteValues["tenant"] = "evil";
            Check(Evaluate(options, request, "/{tenant}").StatusCode == 400, "Unapproved host escaped validation.");
            foreach (var target in new[] { "//evil.example/path", "javascript:alert(1)", "https://user@evil.example", "/bad\\path", "/bad%0d%0aHeader:x", "/bad%", "/{missing}", "/{request.host}", "https://example.com:{route.id}/", "/new?x={route.id}" })
                Reject(() => RedirectEvaluator.Compile(new() { Target = target }, Route("/{id}")), target);
            Reject(() => RedirectEvaluator.Compile(new() { Target = "/old" }, Route("/old")), "Static self-loop");
            Reject(() => RedirectEvaluator.Compile(new() { Target = "https://{request.host}/new" }, Route()), "Missing host allowlist");
            Reject(() => RedirectEvaluator.Compile(new() { Mode = RedirectMode.HttpsOnly }, Route()), "HTTPS allowlist");
            foreach (var host in new[] { "*.example", "example:443", "user@example", "example/path", "" })
                Reject(() => RedirectEvaluator.Compile(new() { Target = "/new", AllowedHosts = [host] }, Route()), host);
            var self = RedirectEvaluator.Compile(new() { Target = "/{route.item}" }, Route("/{item}"));
            request.RouteValues["item"] = "old";
            Check(RedirectEvaluator.Evaluate(self, request).StatusCode == 400, "Dynamic self-loop returned Location.");
            request.RouteValues.Clear();
            Check(RedirectEvaluator.Evaluate(self, request).Location is null, "Missing token returned Location.");
            Reject(() => RedirectEvaluator.Compile(new() { Target = "/" + new string('a', 8192) }, Route()), "Bound exceeded");
            var idn = Evaluate(new() { Target = "https://{request.host}/new", AllowedHosts = ["xn--bcher-kva.example"] }, Request("http://bücher.example/old"));
            Check(idn.Outcome == "Redirect", "IDN allowlist failed.");
            var ip = Evaluate(new() { Mode = RedirectMode.HttpsOnly, AllowedHosts = ["::1"] }, Request("http://[::1]/old"));
            Check(ip.Location == "https://[::1]/old", "IPv6 HTTPS failed: " + ip);
            return Task.CompletedTask;
        });
        await run("Redirect: backend-free requests, policies, metrics, activation, and preview", async () =>
        {
            await using var fixture = await Fixture.Start();
            var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            var route = Route("/old/{item}") with { RouteId = "redirect-test", Match = new() { Path = "/old/{item}", Methods = ["GET", "HEAD", "POST"] } };
            var saved = await config.TryAddRouteConfig(route);
            Check(saved.Success, saved.Message);
            await fixture.WaitRoute(route.RouteId);
            using (var empty = await fixture.Client.GetAsync("/old/one")) Check(empty.StatusCode == HttpStatusCode.ServiceUnavailable, "Clusterless route did not return 503.");
            var binding = await fixture.Bind(route.RouteId, "REDIRECT", "{\"target\":\"/new/{route.item}\"}");
            await fixture.Bind(route.RouteId, "traffic-metrics", "{\"enabled\":true}");
            await fixture.Bind(route.RouteId, "proxy-log", "{}");
            var statistics = fixture.App.Services.GetRequiredService<LockFreeStatistics>();
            var beforeCount = statistics.GetSnapshot().TotalRequests;
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Post })
            {
                using var response = await fixture.Client.SendAsync(new(method, "/old/one?secret=yes"));
                Check((int)response.StatusCode == 307 && response.Headers.Location?.OriginalString == "/new/one", "Redirect response failed.");
                Check((await response.Content.ReadAsByteArrayAsync()).Length == 0, "Redirect contained a body.");
            }
            var metrics = fixture.App.Services.GetRequiredService<PluginMetricStore>();
            Check(metrics.GetRoutes()[route.RouteId].Requests == 3 && metrics.GetDestinations().Count == 0, "Redirect metrics counted incorrectly.");
            Check(statistics.GetSnapshot().TotalRequests - beforeCount == 3, "Logs and metrics double-counted redirects.");
            var logs = fixture.App.Services.GetRequiredService<IProxyLogStore>().GetRecent().Entries;
            Check(logs.Count(e => e.EventType == LogEventType.ProxyResponse && e.RouteId == route.RouteId) == 3, "Redirect logs missing.");
            var snapshot = fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            var version = snapshot.Current.Version;
            using var preview = await fixture.Client.PostAsJsonAsync("/ops/api/plugins/redirect/preview", new
            {
                binding = new { pluginId = "redirect", scope = "Route", scopeId = route.RouteId, configJson = "{\"target\":\"/preview/{route.item}\"}" },
                request = new { url = "http://example.com/old/two", method = "GET" }
            });
            var previewText = await preview.Content.ReadAsStringAsync();
            Check(preview.IsSuccessStatusCode && previewText.Contains("/preview/two"), "Preview failed: " + previewText);
            Check(snapshot.Current.Version == version && metrics.GetRoutes()[route.RouteId].Requests == 3, "Preview changed state or consumed traffic.");
            using var anonymous = new HttpClient();
            using var denied = await anonymous.PostAsJsonAsync(new Uri(fixture.Client.BaseAddress!, "/ops/api/plugins/redirect/preview"), new { });
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Preview was public.");
            await fixture.Bind(route.RouteId, "rate-limit", "{\"enabled\":true,\"permitLimit\":1,\"window\":\"1m\",\"partitionKey\":\"Route\"}");
            using (var first = await fixture.Client.GetAsync("/old/one")) Check((int)first.StatusCode == 307, "Rate limit rejected first redirect.");
            using (var second = await fixture.Client.GetAsync("/old/one")) Check((int)second.StatusCode == 429, "Redirect bypassed rate limiting.");
            var mutation = fixture.App.Services.GetRequiredService<IPluginBindingMutationService>();
            var repository = fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            version = snapshot.Current.Version;
            var original = binding.ConfigJson;
            binding.ConfigJson = "{\"target\":\"//bad\"}";
            try { await mutation.UpsertAsync(binding); throw new Exception("Invalid binding committed."); } catch (ArgumentException) { }
            Check(snapshot.Current.Version == version && (await repository.GetBindingAsync(binding.Id))!.ConfigJson == original, "Invalid activation changed state.");
            binding.ConfigJson = original; binding.Enabled = false;
            await mutation.UpsertAsync(binding);
            using var disabled = await fixture.Client.GetAsync("/old/one");
            Check((int)disabled.StatusCode == 503, "Disabled redirect did not expose backend-free 503.");
        });
    }

    private static RedirectResult Evaluate(RedirectOptions options, HttpRequest request, string pattern = "/old/{**rest}") =>
        RedirectEvaluator.Evaluate(RedirectEvaluator.Compile(options, Route(pattern)), request);
    private static RouteConfig Route(string path = "/old/{**rest}") => new() { RouteId = "r", Match = new() { Path = path } };
    private static HttpRequest Request(string url, string method = "GET")
    {
        var uri = new Uri(url); var request = new DefaultHttpContext().Request;
        request.Scheme = uri.Scheme; request.Host = HostString.FromUriComponent(uri);
        request.Path = PathString.FromUriComponent(uri.AbsolutePath); request.QueryString = new(uri.Query); request.Method = method;
        return request;
    }
    private static void Reject(Action action, string reason) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Accepted invalid input: " + reason); }
    private static void Check(bool condition, string? message) { if (!condition) throw new Exception(message); }

    internal sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string Directory { get; init; }
        private bool OwnsDirectory { get; init; }
        public static async Task<Fixture> Start(Action<IServiceCollection>? configure = null, bool trustedForwarding = false, string? directory = null)
        {
            var ownsDirectory = directory is null;
            directory ??= Path.Combine(Path.GetTempPath(), "wafish-redirect-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Storage:Sqlite:ConnectionString"] = "Data Source=" + Path.Combine(directory, "gateway.db"),
                ["Gateway:Dashboard:RoutePrefix"] = "ops", ["Gateway:Dashboard:Auth:AuthMode"] = "ApiKey",
                ["Gateway:Dashboard:Auth:ApiKey"] = "redirect-test-key"
            });
            builder.Services.AddWafishStorage().AddWafishRuntime().AddWafishAdministration();
            configure?.Invoke(builder.Services);
            var app = builder.Build();
            if (trustedForwarding) app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
                { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
            app.UseWafishGateway(); app.MapWafishAdministration();
            await app.StartAsync();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Api-Key", "redirect-test-key");
            return new() { App = app, Client = client, Directory = directory, OwnsDirectory = ownsDirectory };
        }
        public async Task<PluginBindingEntity> Bind(string routeId, string plugin, string json)
        {
            var route = (await App.Services.GetRequiredService<IRouteRepository>().GetAllRoutesAsync()).Single(r => r.RouteId == routeId);
            var binding = new PluginBindingEntity { PluginId = plugin, PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = routeId, RouteUid = route.RouteUid, ConfigJson = json };
            await App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(binding);
            return binding;
        }
        public async Task WaitRoute(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var endpoints = App.Services.GetRequiredService<EndpointDataSource>();
            while (!endpoints.Endpoints.Any(e => e.Metadata.GetMetadata<RouteModel>()?.Config.RouteId == id))
                await Task.Delay(10, timeout.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await App.StopAsync(); await App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (OwnsDirectory) System.IO.Directory.Delete(Directory, true);
        }
    }
}
