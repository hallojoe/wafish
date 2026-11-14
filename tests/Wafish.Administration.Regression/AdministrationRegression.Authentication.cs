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
    private async Task CheckTwoFactorPersistenceAsync()
    {
        await Check("Two-factor state persists privately and fails on corruption or write failure", () =>
        {
            var statePath = Path.Combine(path, "twofactor-check.json");
            var secret = TotpHelper.GenerateSecret();
            TwoFactorStateStore.Write(statePath, new(true, secret));
            Require(TwoFactorStateStore.Read(statePath, new(false, null)) == new TwoFactorStateStore.State(true, secret), "State did not survive reload.");
            if (!OperatingSystem.IsWindows())
            {
                Require((File.GetUnixFileMode(statePath) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "TOTP secret permissions are too broad.");
            }
    
            foreach (var invalid in new[] { "{", "{}", "null", "{\"Enabled\":true,\"Secret\":null}" })
            {
                File.WriteAllText(statePath, invalid);
                bool failed = false;
                try { TwoFactorStateStore.Read(statePath, new(false, null)); } catch { failed = true; }
                Require(failed, "Corrupted state disabled two-factor authentication.");
            }
            var failedWrite = false;
            try { TwoFactorStateStore.Write(path, new(true, secret)); } catch { failedWrite = true; }
            Require(failedWrite, "Failed persistence reported success.");
            File.Delete(statePath);
            return Task.CompletedTask;
        });
    }

    private async Task CheckApiKeyAccessAsync(WebApplication host, HttpClient client)
    {
            await Check("Administration rejects missing and invalid API credentials", async () =>
            {
                Require((await client.GetAsync("/ops/api/info")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous access succeeded.");
                Require((await client.GetAsync("/api/gateway/routes")).StatusCode == HttpStatusCode.Unauthorized, "Registration REST allowed anonymous access.");
                foreach (var hub in new[] { "traffic", "overview" })
                {
                    Require((await client.PostAsync("/ops/hubs/" + hub + "/negotiate?negotiateVersion=1", null)).StatusCode == HttpStatusCode.Unauthorized,
                        "Anonymous hub negotiation succeeded.");
                }
    
                client.DefaultRequestHeaders.Add("X-Api-Key", "incorrect-key");
                Require((await client.GetAsync("/ops/api/info")).StatusCode == HttpStatusCode.Unauthorized, "Invalid credential succeeded.");
                client.DefaultRequestHeaders.Remove("X-Api-Key");
            });
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            await Check("AI and MCP configuration cannot activate unregistered services", async () =>
            {
                Require(host.Services.GetService<AIService>() == null && host.Services.GetService<AIConfigStore>() == null,
                    "AI services were activated implicitly.");
                Require(!host.Services.GetServices<IHostedService>().Any(s => s is McpServerHost), "MCP was activated implicitly.");
                Require((await client.GetAsync("/ops/api/ai/config")).StatusCode == HttpStatusCode.NotFound, "Disabled AI endpoint is mapped.");
            });
            await Check("Administration APIs work without page or static-asset endpoints", async () =>
            {
                Require(host.Services.GetService<IRazorViewEngine>() == null, "Razor services were registered.");
                foreach (var url in new[] { "/ops", "/_content/Wafish.Dashboard/styles/dashboard-editor.css" })
                {
                    Require((await client.GetAsync(url)).StatusCode == HttpStatusCode.NotFound, "Unexpected presentation endpoint: " + url);
                }
    
                Require((await client.GetAsync("/ops/login")).StatusCode == HttpStatusCode.MethodNotAllowed,
                    "GET login must be unavailable while POST login remains mapped.");
                foreach (var url in new[] { "/ops/api/info", "/ops/api/routes", "/ops/api/clusters", "/ops/api/plugins",
                    "/ops/api/logs/settings", "/ops/api/settings/system", "/ops/api/overview/snapshot", "/ops/api/config/history" })
                {
                    using var result = await ReadJson(await client.GetAsync(url));
                    Require(result.RootElement.ValueKind == JsonValueKind.Object, "Expected a JSON object from " + url);
                }
            });
    }

    private async Task CheckJwtHostAsync()
    {
        await using (var host = await StartHost("DefaultJwt"))
        {
            using var client = Client(host);
            await Check("Corrupted two-factor persistence rejects JWT login", async () =>
            {
                var statePath = Path.Combine(path, "twofactor.json");
                File.WriteAllText(statePath, "{}");
                try
                {
                    using var response = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password = key });
                    Require(response.StatusCode == HttpStatusCode.ServiceUnavailable, "Corrupted state allowed login.");
                    Require(!response.Headers.Contains("Set-Cookie"), "Rejected login issued a session cookie.");
                }
                finally { File.Delete(statePath); }
            });
    
            await Check("Administration login issues a usable JWT without a Razor login page", async () =>
            {
                using var invalid = await ReadJson(await client.PostAsJsonAsync("/ops/login", new { username = "admin", password = "incorrect-key" }));
                Require(invalid.RootElement.GetProperty("code").GetInt32() == 401, "Invalid login succeeded.");
                using var loginResponse = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password = key });
                var cookie = loginResponse.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("dashboard_token=", StringComparison.Ordinal));
                Require(cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("; httponly", StringComparison.OrdinalIgnoreCase), "Session cookie lacks transport or script protection.");
                using var login = await ReadJson(loginResponse);
                var token = login.RootElement.GetProperty("token").GetString();
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                using var info = await ReadJson(await client.GetAsync("/ops/api/info"));
                using var registration = await ReadJson(await client.GetAsync("/api/gateway/routes"));
                client.DefaultRequestHeaders.Authorization = null;
                Require((await client.GetAsync("/ops/api/info?access_token=" + token)).StatusCode == HttpStatusCode.Unauthorized, "URL token authorized a non-hub endpoint.");
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                Require(info.RootElement.GetProperty("code").GetInt32() == 200, "JWT was not accepted.");
            });
            await Check("Administration retains configuration after host restart and supports deletion", async () =>
            {
                using var routes = await ReadJson(await client.GetAsync("/ops/api/routes"));
                Require(routes.RootElement.GetProperty("data").GetRawText().Contains("test-route"), "Route was lost on restart.");
                using var route = await ReadJson(await client.DeleteAsync("/ops/api/config/routes/test-route"));
                using var cluster = await ReadJson(await client.DeleteAsync("/ops/api/config/clusters/test-cluster"));
                using var remaining = await ReadJson(await client.GetAsync("/ops/api/routes"));
                Require(!remaining.RootElement.GetProperty("data").GetRawText().Contains("test-route"), "Deleted route remains visible.");
            });
            await host.StopAsync();
        }
    }

}
