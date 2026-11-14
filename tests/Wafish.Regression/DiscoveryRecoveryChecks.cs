using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Models;
using Wafish.Plugin.ServiceDiscovery;
using Wafish.Plugins;
using Wafish.Services;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;

internal static class DiscoveryRecoveryChecks
{
    private sealed record ProviderCase(string Mode, string Healthy, string Empty, string Unhealthy, string Path, string[] Expected);
    private static readonly ProviderCase[] Providers =
    [
        new("HttpJson", "[\"http://one:80/a\",{\"address\":\"https://two:443/\"},\"http://one/a\"]", "{\"endpoints\":[]}", "[]", "/catalog", ["http://one/a", "https://two/"]),
        new("Consul", "[{\"Service\":{\"Address\":\"\",\"Port\":8080},\"Node\":{\"Address\":\"one\"},\"Checks\":[{\"Status\":\"passing\"}]}]", "[]", "[{\"Service\":{\"Address\":\"one\",\"Port\":8080},\"Checks\":[{\"Status\":\"critical\"}]}]", "/v1/health/service/orders?passing=true", ["http://one:8080/"]),
        new("Nacos", "{\"hosts\":[{\"ip\":\"one\",\"port\":8080,\"healthy\":true}]}", "{\"hosts\":[]}", "{\"hosts\":[{\"ip\":\"one\",\"port\":8080,\"healthy\":false},{\"ip\":\"two\",\"port\":8080,\"enabled\":false}]}", "/nacos/v1/ns/instance/list?serviceName=orders&healthyOnly=true", ["http://one:8080/"]),
        new("Eureka", "{\"application\":{\"instance\":[{\"ipAddr\":\"one\",\"status\":\"UP\",\"port\":{\"$\":8080,\"@enabled\":\"true\"}},{\"hostName\":\"two\",\"port\":{\"$\":80,\"@enabled\":\"false\"},\"securePort\":{\"$\":443,\"@enabled\":\"true\"}}]}}", "{\"application\":{\"instance\":[]}}", "{\"application\":{\"instance\":{\"status\":\"DOWN\"}}}", "/eureka/apps/orders", ["http://one:8080/", "https://two/"]),
        new("Kubernetes", "{\"subsets\":[{\"addresses\":[{\"ip\":\"::1\"}],\"notReadyAddresses\":[{\"ip\":\"10.0.0.2\"}],\"ports\":[{\"port\":8080},{\"port\":53,\"protocol\":\"UDP\"}]}]}", "{\"kind\":\"Endpoints\"}", "{\"subsets\":[{\"notReadyAddresses\":[{\"ip\":\"10.0.0.2\"}],\"ports\":[{\"port\":8080}]}]}", "/api/v1/namespaces/default/endpoints/orders", ["http://[::1]:8080/"])
    ];

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        foreach (var provider in Providers)
        {
            await run($"Discovery provider: {provider.Mode} handles healthy, empty, unhealthy, malformed, timeout, and cancellation responses", async () =>
            {
                await using var fixture = await Fixture.Create();
                var config = new ServiceDiscoveryExecutionConfig { Mode = provider.Mode,
                    Endpoint = provider.Mode == "HttpJson" ? "http://registry/catalog" : "http://registry", ServiceName = "orders" };
                fixture.Handler.Respond = (request, _) =>
                {
                    Check(request.RequestUri!.PathAndQuery == provider.Path, "Wrong provider request URI.");
                    if (provider.Mode == "Eureka") { Check(request.Headers.Accept.Any(v => v.MediaType == "application/json"), "Eureka did not request JSON."); }
                    return Reply(provider.Healthy);
                };
                var endpoints = await fixture.Service.ResolveEndpointsAsync(config, default);
                Check(endpoints.SequenceEqual(provider.Expected), "Healthy endpoints were lost or unhealthy endpoints were included.");
                foreach (var body in new[] { provider.Empty, provider.Unhealthy })
                {
                    fixture.Handler.Respond = (_, _) => Reply(body);
                    Check((await fixture.Service.ResolveEndpointsAsync(config, default)).Count == 0, "Valid empty discovery failed.");
                }
                foreach (var body in new[] { "{broken", "null", "{\"error\":\"failed\"}" })
                {
                    fixture.Handler.Respond = (_, _) => Reply(body);
                    await ExpectFailure<JsonException>(() => fixture.Service.ResolveEndpointsAsync(config, default));
                }
                fixture.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                await ExpectFailure<HttpRequestException>(() => fixture.Service.ResolveEndpointsAsync(config, default));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Handler.Respond = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(); };
                using var cancelled = new CancellationTokenSource();
                var resolving = fixture.Service.ResolveEndpointsAsync(config, cancelled.Token);
                await entered.Task;
                cancelled.Cancel();
                await ExpectFailure<OperationCanceledException>(() => resolving);
                entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                resolving = fixture.Service.ResolveEndpointsAsync(config, default);
                await entered.Task;
                fixture.Clock.Advance(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
                await ExpectFailure<OperationCanceledException>(() => resolving);
            });
        }

        await run("Discovery provider: static endpoints normalize, reject invalid addresses, and observe cancellation", async () =>
        {
            await using var fixture = await Fixture.Create();
            var config = new ServiceDiscoveryExecutionConfig { StaticEndpoints = [" http://one ", "http://one/", "http://one/A", "http://one/a"] };
            Check((await fixture.Service.ResolveEndpointsAsync(config, default)).SequenceEqual(["http://one/", "http://one/A", "http://one/a"]), "Static normalization changed identity.");
            Check((await fixture.Service.ResolveEndpointsAsync(config with { StaticEndpoints = [] }, default)).Count == 0, "Empty static result failed.");
            await ExpectFailure<JsonException>(() => fixture.Service.ResolveEndpointsAsync(config with { StaticEndpoints = ["ftp://one"] }, default));
            await ExpectFailure<OperationCanceledException>(() => fixture.Service.ResolveEndpointsAsync(config, new CancellationToken(true)));
            Check(fixture.Handler.Calls == 0, "Static provider opened an HTTP connection.");
        });

        await run("Discovery health: one failing binding stays degraded through successful peers and skipped cycles", async () =>
        {
            await using var fixture = await Fixture.Create(Binding("bad"), Binding("good"));
            var fail = true;
            fixture.Handler.Respond = (request, _) => request.RequestUri!.Host == "bad" && fail
                ? Task.FromException<HttpResponseMessage>(new IOException("password=seeded-secret")) : Reply("[]");
            await fixture.Service.StartResourceAsync(default);
            await Until(() => fixture.Service.GetBindingHealth().Count == 2);
            var health = await fixture.Service.CheckHealthAsync();
            Check(health.Health == PluginResourceHealthStatus.Degraded && health.Statistics["failedBindings"] == 1, "Healthy peer cleared failure.");
            Check(!health.Message!.Contains("seeded-secret"), "Health exposed provider credentials.");
            var attempts = fixture.Handler.Calls;
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await Until(() => fixture.Clock.TimerCount > 0);
            Check((await fixture.Service.CheckHealthAsync()).Health == PluginResourceHealthStatus.Degraded, "Skipped cycle cleared failure.");
            Check(fixture.Handler.Calls == attempts, "Backoff was ignored.");
            fail = false;
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await Until(() => fixture.Service.GetBindingHealth().All(b => b.LastSuccessAt != null));
            Check((await fixture.Service.CheckHealthAsync()).Health == PluginResourceHealthStatus.Healthy, "Recovered binding remained degraded.");
            await fixture.Service.StopResourceAsync(default);
            Check((await fixture.Service.CheckHealthAsync()).Health == PluginResourceHealthStatus.Stopped, "Stopped discovery retained failures.");
        });

        foreach (var policy in new[] { "RetainLastKnown", "RejectNewTraffic" })
        {
            await run($"Discovery freshness: {policy} enforces elapsed expiry and recovers without changing binding revisions", async () =>
            {
                var binding = Binding("cluster", policy, 10);
                await using var fixture = await Fixture.Create(binding);
                fixture.Handler.Respond = (_, _) => Reply("[\"http://backend/\"]");
                await fixture.Service.RefreshDueBindingsAsync(default);
                var identity = fixture.Publisher.Current.Clusters.Single().Destinations!.Keys.Single();
                var expectedBinding = GatewayPublicationCoordinator.DiscoveryBinding(fixture.Publisher.Current, "cluster");
                fixture.Handler.Respond = (_, _) => Task.FromException<HttpResponseMessage>(new IOException("offline"));
                fixture.Clock.ShiftUtc(TimeSpan.FromDays(3));
                await fixture.Service.RefreshDueBindingsAsync(default);
                Check(!fixture.Service.GetBindingHealth().Single().Stale, "Wall-clock jump expired discovery.");
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                await fixture.Service.RefreshDueBindingsAsync(default);
                Check(fixture.Publisher.Current.Clusters.Single().Destinations!.ContainsKey(identity), "Failure discarded destinations before expiry.");
                fixture.Clock.Advance(TimeSpan.FromSeconds(4));
                await fixture.Service.RefreshDueBindingsAsync(default);
                var beforeExpiryCalls = fixture.Handler.Calls;
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                await fixture.Service.RefreshDueBindingsAsync(default);
                var status = fixture.Service.GetBindingHealth().Single();
                Check(status.Stale && status.StaleAgeSeconds == 10 && status.LastSuccessAt != null, "Stale health lost timing.");
                Check(fixture.Handler.Calls == beforeExpiryCalls, "Expiry incorrectly waited for or restarted the provider request.");
                Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Count == (policy == "RejectNewTraffic" ? 0 : 1), "Wrong stale policy.");
                Check(status.TrafficRejected == (policy == "RejectNewTraffic"), "Wrong rejection health.");
                Check(GatewayPublicationCoordinator.DiscoveryBinding(fixture.Publisher.Current, "cluster") == expectedBinding, "Runtime expiry changed binding revision.");
                var version = fixture.Publisher.Current.Version;
                await fixture.Service.RefreshDueBindingsAsync(default);
                Check(fixture.Publisher.Current.Version == version, "Repeated expiry published another generation.");
                var unrelated = await fixture.Compiler.CompileAsync([], fixture.Config.GetClusters(), version + 1);
                unrelated = GatewayPublicationCoordinator.For(fixture.Publisher).PreserveDiscovery(unrelated);
                fixture.Publisher.Publish(unrelated); fixture.Config.Update(unrelated.Routes, unrelated.Clusters);
                Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Count == (policy == "RejectNewTraffic" ? 0 : 1), "Unrelated publication restored stale destinations.");
                fixture.Handler.Respond = (_, _) => Reply("[\"http://backend/\"]");
                fixture.Clock.Advance(TimeSpan.FromSeconds(10));
                await fixture.Service.RefreshDueBindingsAsync(default);
                Check(fixture.Publisher.Current.Clusters.Single().Destinations!.ContainsKey(identity), "Recovery changed identity.");
                status = fixture.Service.GetBindingHealth().Single();
                Check(status.ConsecutiveFailures == 0 && !status.Stale && !status.TrafficRejected, "Recovery retained stale state.");
                fixture.Handler.Respond = (_, _) => Reply("[]");
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                await fixture.Service.RefreshDueBindingsAsync(default);
                Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Count == 0 && fixture.Service.GetBindingHealth().Single().ConsecutiveFailures == 0,
                    "Valid empty result was treated as provider failure.");
            });
        }

        await run("Discovery freshness: startup failure expires configured destinations and default policy retains them", async () =>
        {
            await using var fixture = await Fixture.Create(Binding("bounded", "RejectNewTraffic", 5), Binding("unlimited"));
            fixture.Handler.Respond = (_, _) => Task.FromException<HttpResponseMessage>(new IOException("offline"));
            await fixture.Service.RefreshDueBindingsAsync(default);
            fixture.Clock.Advance(TimeSpan.FromSeconds(6));
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Publisher.Current.Clusters.Single(c => c.ClusterId == "bounded").Destinations!.Count == 0, "Initial failure never expired.");
            Check(fixture.Publisher.Current.Clusters.Single(c => c.ClusterId == "unlimited").Destinations!.Count == 1, "Default behavior removed last-known destinations.");
            Check(fixture.Service.GetBindingHealth().All(b => b.LastSuccessAt == null), "Failed initial lookup reported success.");
        });

        await run("Discovery scheduling: backoff is bounded, jitter varies retries, and successful empty responses reset failures", async () =>
        {
            await using var first = await Fixture.Create(Binding("cluster"));
            await using var second = await Fixture.Create(Binding("cluster"));
            first.Jitter = 0; second.Jitter = 1;
            first.Handler.Respond = second.Handler.Respond = (_, _) => Task.FromException<HttpResponseMessage>(new IOException("offline"));
            for (var i = 0; i < 10; i++)
            {
                await first.Service.RefreshDueBindingsAsync(default);
                await second.Service.RefreshDueBindingsAsync(default);
                var low = first.Service.GetBindingHealth().Single().NextRefreshSeconds;
                var high = second.Service.GetBindingHealth().Single().NextRefreshSeconds;
                Check(low < high && high <= 300 && low > 0, "Retry jitter or cap failed.");
                var attempts = first.Handler.Calls;
                await first.Service.RefreshDueBindingsAsync(default);
                Check(first.Handler.Calls == attempts, "Backoff did not suppress repeat requests.");
                first.Clock.Advance(TimeSpan.FromSeconds(301)); second.Clock.Advance(TimeSpan.FromSeconds(301));
            }
            first.Handler.Respond = (_, _) => Reply("[]");
            await first.Service.RefreshDueBindingsAsync(default);
            Check(first.Service.GetBindingHealth().Single().NextRefreshSeconds == 5 && first.Service.GetBindingHealth().Single().ConsecutiveFailures == 0,
                "Success did not restore the refresh interval.");
        });

        await run("Discovery publication: changed and disabled bindings reject pending results and clear old health", async () =>
        {
            var binding = Binding("cluster");
            await using var fixture = await Fixture.Create(binding);
            var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Respond = (_, _) => pending.Task;
            var refresh = fixture.Service.RefreshDueBindingsAsync(default);
            Check(!refresh.IsCompleted, "Provider did not block.");
            binding.ConfigJson = "{\"enabled\":true,\"mode\":\"Static\",\"staticEndpoints\":[\"http://replacement/\"]}";
            await fixture.Publish();
            var version = fixture.Publisher.Current.Version;
            pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[\"http://stale/\"]") });
            await refresh;
            Check(fixture.Publisher.Current.Version == version, "Stale provider result changed the new binding.");
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Publisher.Current.Clusters.Single().Destinations!.Values.Single().Address == "http://replacement/", "Changed binding did not refresh immediately.");
            binding.Enabled = false;
            await fixture.Publish();
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Service.GetBindingHealth().Count == 0, "Disabled binding retained health state.");
        });

        await run("Discovery health: recovery clears only its binding and removal clears only retired health", async () =>
        {
            var first = Binding("first");
            var second = Binding("second");
            await using var fixture = await Fixture.Create(first, second);
            fixture.Handler.Respond = (_, _) => Task.FromException<HttpResponseMessage>(new IOException("offline"));
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check((await fixture.Service.CheckHealthAsync()).Statistics["failedBindings"] == 2, "Did not record both provider failures.");
            fixture.Handler.Respond = (request, _) => request.RequestUri!.Host == "first" ? Reply("[]")
                : Task.FromException<HttpResponseMessage>(new IOException("offline"));
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check(fixture.Service.GetBindingHealth().Single(b => b.ClusterId == "first").ConsecutiveFailures == 0 &&
                fixture.Service.GetBindingHealth().Single(b => b.ClusterId == "second").ConsecutiveFailures == 2,
                "Recovering one binding cleared its failing peer.");
            second.Enabled = false;
            await fixture.Publish();
            await fixture.Service.RefreshDueBindingsAsync(default);
            Check((await fixture.Service.CheckHealthAsync()).Statistics["failedBindings"] == 0, "Disabled provider retained a failure.");
        });

        await run("Discovery traffic: stale expiry returns 503 and a recovered provider restores forwarding", async () =>
        {
            static WebApplication Host()
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                return builder.Build();
            }
            await using var backend = Host();
            backend.MapGet("/probe", () => Results.Text("discovered"));
            await backend.StartAsync();
            static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var fixture = await Fixture.Create(Binding("cluster", "RejectNewTraffic", 10));
            fixture.Config.Update([new RouteConfig { RouteId = "probe", ClusterId = "cluster", Match = new() { Path = "/probe" } }], fixture.Config.GetClusters());
            await fixture.Publish();
            fixture.Handler.Respond = (_, _) => Reply(JsonSerializer.Serialize(new[] { Address(backend) }));
            await fixture.Service.RefreshDueBindingsAsync(default);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddReverseProxy();
            builder.Services.AddSingleton<IProxyConfigProvider>(fixture.Config);
            await using var gateway = builder.Build();
            gateway.MapReverseProxy();
            await gateway.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(5) };
            Check(await client.GetStringAsync("/probe") == "discovered", "Initial discovery did not forward.");
            fixture.Handler.Respond = (_, _) => Task.FromException<HttpResponseMessage>(new IOException("offline"));
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            await fixture.Service.RefreshDueBindingsAsync(default);
            await UntilAsync(async () => { using var reply = await client.GetAsync("/probe"); return reply.StatusCode == HttpStatusCode.ServiceUnavailable; });
            fixture.Handler.Respond = (_, _) => Reply(JsonSerializer.Serialize(new[] { Address(backend) }));
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await fixture.Service.RefreshDueBindingsAsync(default);
            await UntilAsync(async () => { using var reply = await client.GetAsync("/probe"); return reply.IsSuccessStatusCode && await reply.Content.ReadAsStringAsync() == "discovered"; });
            await gateway.StopAsync();
            await backend.StopAsync();
        });

        await run("Discovery configuration: stale policy schema matches execution defaults and rejects unsupported settings", () =>
        {
            var schema = new HttpServiceDiscoveryPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
            var validator = new PluginConfigurationSchemaValidator();
            foreach (var policy in new[] { "RetainLastKnown", "RejectNewTraffic" })
            {
                var json = JsonSerializer.Serialize(new { enabled = true, mode = "Static", maxStaleSeconds = 10, stalePolicy = policy });
                Check(validator.TryValidate(json, schema, out _, out _), "Valid stale policy failed schema validation.");
                var config = JsonSerializer.Deserialize<ServiceDiscoveryExecutionConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Check(config.MaxStaleSeconds == 10 && config.StalePolicy == policy, "Execution config lost stale policy.");
            }
            foreach (var json in new[] { "{\"enabled\":true,\"mode\":\"Static\",\"maxStaleSeconds\":-1}", "{\"enabled\":true,\"mode\":\"Static\",\"stalePolicy\":\"Ignore\"}" })
            { Check(!validator.TryValidate(json, schema, out _, out _), "Invalid stale policy was accepted."); }
            var defaults = new ServiceDiscoveryExecutionConfig();
            Check(defaults.MaxStaleSeconds == 0 && defaults.StalePolicy == "RetainLastKnown", "Defaults changed existing behavior.");
            return Task.CompletedTask;
        });
    }

    private static PluginBindingEntity Binding(string cluster, string policy = "RetainLastKnown", int maxStale = 0) => new()
    {
        Id = cluster + "-binding", PluginId = "service-discovery", PluginVersion = "1.0", Scope = PluginBindingScope.Cluster,
        ScopeId = cluster, Enabled = true,
        ConfigJson = JsonSerializer.Serialize(new { enabled = true, mode = "HttpJson", endpoint = "http://" + cluster, refreshSeconds = 5,
            maxStaleSeconds = maxStale, stalePolicy = policy })
    };
    private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    private static async Task ExpectFailure<T>(Func<Task> action) where T : Exception
    {
        try { await action(); throw new Exception("Expected " + typeof(T).Name); }
        catch (T) { }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(5, timeout.Token); }
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) { await Task.Delay(5, timeout.Token); }
    }
    private static void Check(bool value, string message) { if (!value) { throw new Exception(message); } }

    internal sealed class Handler : HttpMessageHandler, IHttpClientFactory
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond = (_, _) => Reply("[]");
        public int Calls;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Respond(request, cancellationToken); }
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public readonly ReviewReleaseChecks.ManualTime Clock = new();
        public readonly Handler Handler = new();
        public readonly GatewaySnapshotPublisher Publisher = new();
        public readonly WafishProxyConfigProvider Config;
        public readonly GatewaySnapshotCompiler Compiler;
        public readonly ServiceDiscoveryRefreshService Service;
        public double Jitter;
        private Fixture(PluginBindingEntity[] bindings)
        {
            var clusters = bindings.Select(b => new ClusterConfig { ClusterId = b.ScopeId, Destinations = new Dictionary<string, DestinationConfig>
                { ["original"] = new() { Address = "http://original/" } } }).ToArray();
            Config = new([], clusters);
            Compiler = new(new InMemoryPluginRepository(bindings));
            Service = new(new GatewayPluginExecutionPlanProvider(Publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance),
                Config, Compiler, Publisher, Handler, NullLogger<ServiceDiscoveryRefreshService>.Instance, Clock, () => Jitter);
        }
        public static async Task<Fixture> Create(params PluginBindingEntity[] bindings)
        { var fixture = new Fixture(bindings); await fixture.Publish(); return fixture; }
        public async Task Publish()
        {
            var snapshot = await Compiler.CompileAsync(Config.GetRoutes(), Config.GetClusters(), Publisher.Current.Version + 1);
            Publisher.Publish(snapshot); Config.Update(snapshot.Routes, snapshot.Clusters);
        }
        public async ValueTask DisposeAsync() { await Service.StopResourceAsync(default); Handler.Dispose(); }
    }
}
