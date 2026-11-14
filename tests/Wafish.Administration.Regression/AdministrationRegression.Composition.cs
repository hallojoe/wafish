using Wafish.Runtime;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Plugin.ProxyLog.Services;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Wafish.Administration;
using Wafish.Dashboard.Infrastructure.I18n;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed partial class AdministrationRegressionSuite
{
    private async Task CheckDeploymentAsync()
    {
        await Check("Administration deploys without the dashboard assembly or Razor assets", () =>
        {
            Require(!File.Exists(Path.Combine(AppContext.BaseDirectory, "Wafish.Dashboard.dll")), "Dashboard assembly was deployed.");
            Require(!typeof(AdministrationServiceCollectionExtensions).Assembly.GetReferencedAssemblies().Any(a => a.Name == "Wafish.Dashboard"),
                "Administration references presentation.");
            Require(DashboardI18n.EnUS.Count > 100, "Shared translations did not load.");
            var request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
            request.Headers.Cookie = "dashboard_locale=en-US";
            Require(DashboardI18n.ResolveLocale(request) == "en-US", "Legacy locale cookie was ignored.");
            request.Headers["X-Gateway-Locale"] = "fr-FR";
            Require(DashboardI18n.ResolveLocale(request) == "en-US", "Unsupported locale did not fall back to English.");
            request.Headers["X-Gateway-Locale"] = "unsupported";
            Require(DashboardI18n.ResolveLocale(request) == "en-US", "Invalid locale did not fall back to the cookie.");
            request.Headers.Cookie = "dashboard_locale=fr-FR";
            Require(DashboardI18n.ResolveLocale(request) == "en-US", "Unsupported cookie did not fall back to English.");
            Require(DashboardI18n.GetDict("fr-FR") == DashboardI18n.EnUS, "Unsupported dictionary did not fall back to English.");
            Require(!typeof(DashboardI18n).Assembly.GetManifestResourceNames().Any(name => name.Contains(".zh_")), "Unsupported locale resources remain embedded.");
            return Task.CompletedTask;
        });
    }

    private async Task CheckOptionalFeaturesAsync()
    {
        await using (var host = await StartHost("ApiKey", enableAi: true))
        {
            using var client = Client(host);
            await Check("Explicit AI registration resolves services and preserves administration authentication", async () =>
            {
                Require((await client.GetAsync("/ops/api/ai/config")).StatusCode == HttpStatusCode.Unauthorized, "AI API bypassed authentication.");
                client.DefaultRequestHeaders.Add("X-Api-Key", key);
                using var config = await ReadJson(await client.GetAsync("/ops/api/ai/config"));
                Require(!config.RootElement.GetProperty("mcpServer").GetProperty("registered").GetBoolean(), "AI implicitly enabled MCP.");
                _ = host.Services.GetRequiredService<AIService>();
                Require((await client.PostAsJsonAsync("/ops/api/ai/config", new { mcpServer = new { enabled = true } })).StatusCode == HttpStatusCode.BadRequest,
                    "Unregistered MCP was enabled through the configuration API.");
            });
            await host.StopAsync();
        }
        await Check("MCP binds loopback, enforces parent credentials, rejects origins, and limits request bodies", async () =>
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var mcpPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            await using var host = await StartHost("ApiKey", enableMcp: true, mcpPort: mcpPort);
            using var mcp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{mcpPort}"), Timeout = TimeSpan.FromSeconds(10) };
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try
                {
                    using var response = await mcp.GetAsync("/mcp", ready.Token);
                    Require(response.StatusCode == HttpStatusCode.Unauthorized, "MCP allowed anonymous access.");
                    break;
                }
                catch (HttpRequestException) { await Task.Delay(50, ready.Token); }
            }
            mcp.DefaultRequestHeaders.Add("X-Api-Key", "invalid");
            Require((await mcp.GetAsync("/mcp")).StatusCode == HttpStatusCode.Unauthorized, "MCP accepted invalid credentials.");
            mcp.DefaultRequestHeaders.Remove("X-Api-Key");
            mcp.DefaultRequestHeaders.Add("X-Api-Key", key);
            mcp.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
            Require((await mcp.GetAsync("/mcp")).StatusCode == HttpStatusCode.Forbidden, "MCP accepted an untrusted browser origin.");
            mcp.DefaultRequestHeaders.Remove("Origin");
            mcp.DefaultRequestHeaders.Add("Origin", mcp.BaseAddress!.GetLeftPart(UriPartial.Authority));
            using var initialized = await ReadJson(await mcp.PostAsJsonAsync("/mcp", new
            { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } }));
            Require(initialized.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "Wafish.Mcp", "MCP initialization failed.");
            using var tools = await ReadJson(await mcp.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 2, method = "tools/list" }));
            Require(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 6, "MCP read tools are unavailable.");
            using var call = await ReadJson(await mcp.PostAsJsonAsync("/mcp", new
            { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "list_routes", arguments = new { } } }));
            Require(call.RootElement.TryGetProperty("result", out _), "MCP tool call failed.");
            using var oversized = await mcp.PostAsync("/mcp", new StringContent(new string('x', 65537), Encoding.UTF8, "application/json"));
            Require(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "MCP body limit was bypassed.");
            host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Wafish.Dashboard.Infrastructure.DashboardOptions>>().Value.Auth.Permissions = [];
            Require((await mcp.GetAsync("/mcp")).StatusCode == HttpStatusCode.Forbidden, "MCP ignored operation permissions.");
            await host.StopAsync();
            using var socket = new System.Net.Sockets.TcpClient();
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, mcpPort);
                throw new Exception("MCP listener survived parent shutdown.");
            }
            catch (System.Net.Sockets.SocketException) { }
        });
    
        foreach (var environment in new[] { Environments.Development, Environments.Staging, Environments.Production })
        {
            await Check("Anonymous administration requires explicit loopback permission in " + environment, async () =>
            {
                try
                {
                    await using var unsafeHost = await StartHost("None", environment: environment);
                    throw new Exception("Anonymous administration started without explicit permission.");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("requires authentication", StringComparison.Ordinal)) { }
                await using var localHost = await StartHost("None", environment: environment, allowAnonymousLoopback: true);
                using var localClient = Client(localHost);
                (await localClient.GetAsync("/ops/api/info")).EnsureSuccessStatusCode();
                localClient.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
                Require((await localClient.GetAsync("/ops/api/info")).StatusCode == HttpStatusCode.Forbidden,
                    "Forwarded anonymous request passed the loopback restriction.");
            });
        }
    
        await Check("Explicit MCP registration is independent and idempotent", () =>
        {
            var services = new ServiceCollection();
            services.AddWafishMcp();
            services.AddWafishMcp();
            Require(services.Count(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(McpServerHost)) == 1,
                "MCP hosted service was duplicated.");
            Require(!services.Any(s => s.ServiceType == typeof(AIService)), "MCP implicitly registered AI chat.");
            return Task.CompletedTask;
        });
    }

}
