using System.IO.Compression;
using System.Text;
using Wafish.Runtime;
using Wafish.Services;
using Wafish.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Wafish.Dashboard.Infrastructure.Deployment;
using Wafish.Dashboard.Infrastructure.Middleware;
using Wafish.Infrastructure.Middleware;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

internal static class RuntimeHostChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Runtime: starts and proxies without dashboard registration", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "aneiang-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                var upstreamBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
                upstreamBuilder.Logging.ClearProviders();
                upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
                await using var upstream = upstreamBuilder.Build();
                var upstreamCalls = 0;
                upstream.MapGet("/hello", (HttpContext context) =>
                {
                    Interlocked.Increment(ref upstreamCalls);
                    context.Response.Headers.CacheControl = "public, max-age=60";
                    return "runtime-ok";
                });
                var compressibleBody = new string('x', 100000);
                upstream.MapGet("/large", (HttpContext context) =>
                {
                    Interlocked.Increment(ref upstreamCalls);
                    context.Response.Headers.CacheControl = "public, max-age=60";
                    return compressibleBody;
                });
                await upstream.StartAsync();

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                { EnvironmentName = Environments.Development, ContentRootPath = path });
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Storage:Sqlite:ConnectionString"] = "Data Source=" + Path.Combine(path, "runtime.db"),
                    ["Gateway:Deployment:Mode"] = "ProxyOnly",
                    ["Gateway:Transforms:AddResponseHeaders:X-Runtime"] = "active",
                    ["ReverseProxy:Routes:limited:ClusterId"] = "upstream",
                    ["ReverseProxy:Routes:limited:Match:Path"] = "/limited",
                    ["ReverseProxy:Routes:test:ClusterId"] = "upstream",
                    ["ReverseProxy:Routes:test:Match:Path"] = "/{**path}",
                    ["ReverseProxy:Clusters:upstream:Destinations:one:Address"] = Address(upstream)
                });
                builder.Services.AddWafIshStorage();
                builder.Services.AddWafIshRuntime();
                builder.Services.AddWafIshRuntime();
                await using var app = builder.Build();
                await app.Services.GetRequiredService<IPluginConfigurationRepository>().UpsertBindingAsync(new PluginBindingEntity
                {
                    PluginId = "response-cache", PluginVersion = "1.0", Scope = PluginBindingScope.Route,
                    ScopeId = "test", ConfigJson = "{\"enabled\":true}"
                });
                await app.Services.GetRequiredService<IPluginConfigurationRepository>().UpsertBindingAsync(new PluginBindingEntity
                {
                    PluginId = "compression", PluginVersion = "1.0", Scope = PluginBindingScope.Route,
                    ScopeId = "test", ConfigJson = "{\"enabled\":true}"
                });
                await app.Services.GetRequiredService<IPluginConfigurationRepository>().UpsertBindingAsync(new PluginBindingEntity
                {
                    PluginId = "rate-limit-redis", PluginVersion = "1.0", Scope = PluginBindingScope.Route,
                    ScopeId = "limited", ConfigJson = "{\"enabled\":true,\"failureMode\":\"FailClosed\"}"
                });
                app.UseRouting();
                var administrationConfigured = false;
                app.UseWafIshGateway(configureAdministration: _ => administrationConfigured = true);
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri(Address(app)), Timeout = TimeSpan.FromSeconds(10) };
                var response = await client.GetStringAsync("/hello");
                if (response != "runtime-ok")
                {
                    throw new InvalidOperationException($"Gateway response was incorrect: {response}");
                }

                using var cached = await client.GetAsync("/hello");
                if (await cached.Content.ReadAsStringAsync() != "runtime-ok" || upstreamCalls != 1)
                {
                    throw new InvalidOperationException($"Configured cache plugin did not run: calls={upstreamCalls}, headers={cached.Headers}, body={await cached.Content.ReadAsStringAsync()}");
                }

                using (var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/hello"))
                {
                    crossOrigin.Headers.Add("Origin", "https://untrusted.example");
                    using var crossOriginResponse = await client.SendAsync(crossOrigin);
                    if (crossOriginResponse.Headers.Contains("Access-Control-Allow-Origin"))
                    {
                        throw new InvalidOperationException("The default gateway granted cross-origin access.");
                    }
                }
                foreach (var encoding in new[] { "gzip", "br", "identity" })
                {
                    var before = upstreamCalls;
                    for (var i = 0; i < 2; i++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, "/large");
                        request.Headers.AcceptEncoding.ParseAdd(encoding);
                        using var compressed = await client.SendAsync(request);
                        compressed.EnsureSuccessStatusCode();
                        if (encoding != "identity" && !compressed.Content.Headers.ContentEncoding.Contains(encoding))
                        {
                            throw new InvalidOperationException($"Missing encoding {encoding} on request {i}: {compressed.Headers} {compressed.Content.Headers}");
                        }

                        using var bytes = new MemoryStream(await compressed.Content.ReadAsByteArrayAsync());
                        using Stream decoded = encoding switch { "gzip" => new GZipStream(bytes, CompressionMode.Decompress), "br" => new BrotliStream(bytes, CompressionMode.Decompress), _ => bytes };
                        using var reader = new StreamReader(decoded, Encoding.UTF8);
                        if (await reader.ReadToEndAsync() != compressibleBody)
                        {
                            throw new InvalidOperationException("Compressed HTTP body mismatch.");
                        }

                        if (!compressed.Headers.Vary.Contains("Accept-Encoding"))
                        {
                            throw new InvalidOperationException("Missing compression Vary.");
                        }
                    }
                    if (upstreamCalls != before + 1)
                    {
                        throw new InvalidOperationException("Compressed HTTP variant did not cache.");
                    }
                }
                if (!cached.Headers.Contains("X-Runtime"))
                {
                    throw new InvalidOperationException("Runtime response transforms were skipped.");
                }

                if (administrationConfigured)
                {
                    throw new InvalidOperationException("Proxy-only mode configured administration.");
                }

                using var unavailable = await client.GetAsync("/limited");
                if (unavailable.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    throw new InvalidOperationException("Redis pipeline failure policy was skipped.");
                }

                foreach (var endpoint in new[] { "/live", "/ready", "/health" })
                {
                    (await client.GetAsync(endpoint)).EnsureSuccessStatusCode();
                }

                var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
                if (actions.OfType<ControllerActionDescriptor>().Any(a => a.ControllerTypeInfo.Namespace?.Contains("Dashboard") == true))
                {
                    throw new InvalidOperationException("Runtime registered dashboard controllers.");
                }

                if (app.Services.GetServices<IHostedService>().Count(s => s is DynamicYarpConfigService) != 1)
                {
                    throw new InvalidOperationException("Configuration startup was registered more than once.");
                }

                if (typeof(GatewayRuntimeExtensions).Assembly.GetReferencedAssemblies().Any(a =>
                    a.Name is "WafIsh.Dashboard" or "WafIsh.Administration"))
                {
                    throw new InvalidOperationException("Runtime depends on administration or presentation.");
                }

                await app.StopAsync();
                await upstream.StopAsync();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(path, true);
            }
        });
    }

    public static async Task CheckEndpointModesAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Proxy:Url"] = "http://127.0.0.1:5100",
            ["Kestrel:Endpoints:Dashboard:Url"] = "http://127.0.0.1:5200"
        }).Build();
        foreach (var (mode, port, path, allowed) in new[]
        {
            (DeploymentMode.Split, 5100, "/console/configuration/routes", false),
            (DeploymentMode.Split, 5200, "/console/configuration/routes", true),
            (DeploymentMode.Split, 5100, "/console-proxy", true),
            (DeploymentMode.Split, 5100, "/hello", true), (DeploymentMode.Split, 5100, "/ops/api/info", false),
            (DeploymentMode.Split, 5200, "/ops/api/info", true), (DeploymentMode.Split, 5200, "/hello", false),
            (DeploymentMode.ProxyOnly, 5100, "/hello", true), (DeploymentMode.ProxyOnly, 5100, "/ops", false),
            (DeploymentMode.DashboardOnly, 5200, "/ops", true), (DeploymentMode.DashboardOnly, 5200, "/hello", false)
        })
        {
            var options = new DeploymentOptions { Mode = mode };
            var passed = false;
            var middleware = new EndpointRouterMiddleware(_ => { passed = true; return Task.CompletedTask; },
                new EndpointRoleResolver(config, options), Options.Create(options), NullLogger<EndpointRouterMiddleware>.Instance,
                Options.Create(new GatewayMiddlewareOptions { RoutePrefix = "ops", AdditionalAdministrationPrefixes = ["console"] }));
            var context = new DefaultHttpContext();
            context.Connection.LocalPort = port;
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);
            if (passed != allowed)
            {
                throw new InvalidOperationException($"Unexpected endpoint access: {mode}, {port}, {path}");
            }
        }
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
