using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugin.Cache;
using Wafish.Plugin.Compression;
using Wafish.Plugins;
using Wafish.ReactUi;
using Wafish.Runtime;
using Wafish.Services;
using Wafish.Storage.Sqlite;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class ReviewDeploymentChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Deployment: shell and embedded assets include PathBase and custom prefixes", CheckUiAsync);
        await run("Lifecycle: shutdown joins every retired domain and rejects late work", CheckRetirementAsync);
        await run("Lifecycle: disposal failures remain observable through repeated shutdown", CheckFailedDisposalAsync);
        await run("Lifecycle: failed preparation releases its constructed provider", async () =>
        {
            var plugin = new OwnedPlugin { FailPreparation = true };
            await using var root = Root(plugin);
            await using var manager = Manager(root);
            Check(!(await manager.TransitionAsync([plugin.PluginId])).Succeeded, "Invalid preparation was published.");
            Check(plugin.Resources.Single().Disposals == 1, "Failed preparation leaked its provider.");
        });
        await run("Policy: deprecated cache switches preserve host, query, header, and credential isolation", CheckCacheAsync);
        await run("Policy: compression thresholds are independent of write chunk size", CheckCompressionAsync);
    }

    private static async Task CheckUiAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wafish-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Storage:Sqlite:ConnectionString"] = "Data Source=" + Path.Combine(directory, "gateway.db"),
                ["Gateway:Dashboard:Auth:AuthMode"] = "None",
                ["Gateway:Security:ControlPlane:AllowAnonymousLoopback"] = "true",
                ["Gateway:Dashboard:RoutePrefix"] = "manage/api",
                ["Gateway:ReactUi:RoutePrefix"] = "manage/ui"
            });
            builder.Services.AddWafishStorage().AddWafishRuntime().AddWafishReactUi();
            await using var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            app.UsePathBase("/edge");
            app.MapWafishReactUi();
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                foreach (var prefix in new[] { "", "/edge" })
                {
                    var html = await client.GetStringAsync(prefix + "/manage/ui/configuration/routes");
                    Check(html.Contains($"<base href=\"{prefix}/manage/ui/\">") && html.Contains($"\"apiBase\":\"{prefix}/manage/api\"") && html.Contains($"\"uiBase\":\"{prefix}/manage/ui\""), "Shell omitted deployment prefixes.");
                    var asset = System.Text.RegularExpressions.Regex.Match(html, "src=\"\\./(assets/[^\"]+)\"").Groups[1].Value;
                    Check(asset.Length > 0, "Shell entry asset missing.");
                    using var response = await client.GetAsync(prefix + "/manage/ui/" + asset);
                    Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.Extensions.Any(e => e.Name == "immutable") == true, "Embedded entry asset unavailable.");
                }
                using var missing = await client.GetAsync("/edge/manage/ui/assets/missing.js");
                Check(missing.StatusCode == System.Net.HttpStatusCode.NotFound, "Missing asset returned the shell.");
            }
            finally { await app.StopAsync(); }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static ServiceProvider Root(IGatewayPlugin plugin) => new ServiceCollection().AddLogging()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
        .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Directory.GetCurrentDirectory()))
        .AddSingleton<IPluginResourceMonitor, TestMonitor>()
        .AddSingleton(plugin).BuildServiceProvider();

    private static PluginRuntimeDomainManager Manager(ServiceProvider root) => new(root,
        new ExternalGatewayPluginHost(root.GetRequiredService<IHostEnvironment>(), root.GetRequiredService<IConfiguration>(), NullLogger<ExternalGatewayPluginHost>.Instance),
        NullLogger<PluginRuntimeDomainManager>.Instance);

    private static async Task CheckRetirementAsync()
    {
        var plugin = new OwnedPlugin();
        await using var root = Root(plugin);
        var manager = Manager(root);
        var leases = new List<PluginRuntimeDomainLease>();
        var domains = new List<PluginRuntimeDomain>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                Check((await manager.TransitionAsync([plugin.PluginId])).Succeeded, "Transition failed.");
                domains.Add(manager.Current);
                Check(manager.Current.TryAcquire(out var lease), "Request lease failed.");
                leases.Add(lease);
            }
            await using var late = await manager.PrepareAsync([plugin.PluginId]);
            var shutdown = manager.DisposeAsync().AsTask();
            var second = manager.DisposeAsync().AsTask();
            Check(!shutdown.IsCompleted && ReferenceEquals(shutdown, second), "Shutdown abandoned retirement ownership.");
            try { await late.CommitAsync(); throw new Exception("Shutdown accepted a prepared domain."); }
            catch (ObjectDisposedException) { }
            try { await manager.PrepareAsync([]); throw new Exception("Shutdown accepted preparation."); }
            catch (ObjectDisposedException) { }
            try { await manager.InvokeMiddlewareAsync(new DefaultHttpContext(), _ => Task.CompletedTask); throw new Exception("Shutdown accepted a request."); }
            catch (ObjectDisposedException) { }
            Check(plugin.Resources.Take(4).All(r => r.Disposals == 0), "A leased domain was disposed.");
            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            leases.Clear();
            await Task.WhenAll(shutdown, second).WaitAsync(TimeSpan.FromSeconds(5));
            Check(plugin.Resources.All(r => r.Disposals == 1), "A domain was leaked or disposed twice.");
            Check(domains.All(d => !d.TryAcquire(out _)), "Retired domain accepted a new lease.");
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            await manager.DisposeAsync();
        }
    }

    private static async Task CheckFailedDisposalAsync()
    {
        var plugin = new OwnedPlugin { FailDisposal = true };
        await using var root = Root(plugin);
        var manager = Manager(root);
        Check((await manager.TransitionAsync([plugin.PluginId])).Succeeded, "Transition failed.");
        Check((await manager.TransitionAsync([])).Succeeded, "Retirement blocked activation.");
        for (var i = 0; i < 2; i++)
        {
            try { await manager.DisposeAsync(); throw new Exception("Disposal failure was hidden."); }
            catch (AggregateException) { }
        }
        Check(plugin.Resources.Single().Disposals == 1, "Failed disposal was repeated.");
    }

    private static async Task CheckCacheAsync()
    {
        using var schema = JsonDocument.Parse(new ResponseCachePlugin().Manifest.Schemas.Single().ConfigJsonSchema);
        foreach (var name in new[] { "varyByQuery", "varyHeaders" })
        {
            var property = schema.RootElement.GetProperty("properties").GetProperty(name);
            Check(property.GetProperty("deprecated").GetBoolean() && property.GetProperty("description").GetString()!.Contains("ignored"), "Ineffective cache setting lacks a deprecation message.");
        }
        foreach (var settings in new[] { "{\"enabled\":true,\"varyByQuery\":false,\"varyHeaders\":[]}", "{\"enabled\":true,\"varyByQuery\":true,\"varyHeaders\":[\"X-Variant\"]}" })
        {
            var plans = Plans("response-cache", settings);
            var store = new ResponseCacheStore(Options.Create(new ResponseCacheBudgetOptions()));
            var calls = 0;
            var middleware = new ResponseCacheMiddleware(async c =>
            {
                calls++;
                c.Response.Headers.CacheControl = "public,max-age=60";
                c.Response.Headers.Vary = "X-Variant";
                await c.Response.WriteAsync(calls.ToString());
            }, store, plans);
            async Task<string> Send(string host = "one.test", string query = "?id=1", string variant = "one", bool credential = false)
            {
                var context = Context();
                context.Request.Host = new(host); context.Request.QueryString = new(query);
                context.Request.Headers["X-Variant"] = variant;
                if (credential)
                {
                    context.Request.Headers.Authorization = "Bearer private";
                }

                await middleware.InvokeAsync(context);
                return Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
            }
            Check(await Send() == "1" && await Send() == "1", "Repeated variant missed cache.");
            Check(await Send(query: "?id=2") == "2", "Query variants collided.");
            Check(await Send(host: "two.test") == "3", "Host variants collided.");
            Check(await Send(variant: "two") == "4", "Header variants collided.");
            Check(await Send(credential: true) == "5" && await Send(credential: true) == "6", "Credentials entered shared cache.");
            Check(await Send() == "1", "Private response replaced public cache.");
        }
    }

    private static async Task CheckCompressionAsync()
    {
        using var schema = JsonDocument.Parse(new CompressionPlugin().Manifest.Schemas.Single().ConfigJsonSchema);
        Check(schema.RootElement.GetProperty("properties").GetProperty("minResponseSize").GetProperty("description").GetString()!.Contains("65536"), "Compression bound is undocumented.");
        foreach (var threshold in new[] { 1024, 65536, 65537 })
        {
            foreach (var known in new[] { false, true })
            {
                foreach (var chunked in new[] { false, true })
                {
                    foreach (var shortBody in new[] { false, true })
        {
            var payload = Encoding.UTF8.GetBytes(new string('x', shortBody ? threshold - 1 : threshold + 7));
            var context = Context();
            context.Request.Headers.AcceptEncoding = "gzip";
            var middleware = new CompressionMiddleware(async c =>
            {
                c.Response.ContentType = "text/plain";
                if (known)
                {
                    c.Response.ContentLength = payload.Length;
                }

                var chunk = chunked ? 127 : payload.Length;
                for (var i = 0; i < payload.Length; i += chunk)
                {
                    await c.Response.Body.WriteAsync(payload.AsMemory(i, Math.Min(chunk, payload.Length - i)));
                }
            }, Plans("compression", JsonSerializer.Serialize(new { enabled = true, minResponseSize = threshold })));
            await middleware.InvokeAsync(context);
            var compressed = context.Response.Headers.ContentEncoding == "gzip";
            Check(compressed == (!shortBody && (known || threshold <= 65536)), $"Threshold {threshold}, known={known}, chunked={chunked}, short={shortBody} selected incorrectly.");
            var output = (MemoryStream)context.Response.Body; output.Position = 0;
            using var restored = new MemoryStream();
            if (compressed) { await using var decoder = new GZipStream(output, CompressionMode.Decompress, true); await decoder.CopyToAsync(restored); }
            else
                        {
                            await output.CopyToAsync(restored);
                        }

                        Check(restored.ToArray().SequenceEqual(payload), "Compression changed response content.");
        }
                }
            }
        }
    }

    private static readonly RouteConfig Route = new() { RouteId = "policy", Match = new() { Path = "/policy" } };
    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Path = "/policy";
        context.Response.Body = new MemoryStream();
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(Route, null!, HttpTransformer.Default) });
        return context;
    }
    private static GatewayPluginExecutionPlanProvider Plans(string plugin, string json)
    {
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [Route],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(Route.RouteId,
                [new("policy", plugin, PluginBindingScope.Route, Route.RouteId, json, 1, 1, 0)]) });
        return new(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
    }
    private sealed class TestMonitor : IPluginResourceMonitor
    {
        public IReadOnlyList<PluginResourceUsage> GetAllUsage() => [];
        public PluginResourceUsage? GetUsage(string pluginId) => null;
        public void RecordRequest(string pluginId, long elapsedMs, bool succeeded) { }
        public PluginResourceUsageTotals GetTotals() => new(0, 0, 0, 0, 0, 0, 0);
    }
    private sealed class OwnedPlugin : IGatewayPlugin
    {
        public List<OwnedResource> Resources { get; } = [];
        public bool FailDisposal { get; init; }
        public bool FailPreparation { get; init; }
        public string PluginId => "waf";
        public string DisplayName => "Owned test resource";
        public string Version => "1.0";
        public PluginManifest Manifest => new(PluginId, DisplayName, Version, [], [], 0, new(), [], "test");
        public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) => services.AddSingleton(_ =>
        {
            var resource = new OwnedResource(FailDisposal); Resources.Add(resource); return resource;
        });
        public void ConfigureMiddleware(IApplicationBuilder app)
        {
            _ = app.ApplicationServices.GetRequiredService<OwnedResource>();
            if (FailPreparation)
            {
                throw new InvalidOperationException("Injected preparation failure");
            }
        }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    }
    private sealed class OwnedResource(bool fail) : IAsyncDisposable
    {
        public int Disposals;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return fail ? ValueTask.FromException(new IOException("Injected disposal failure")) : ValueTask.CompletedTask; }
    }
    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
}
