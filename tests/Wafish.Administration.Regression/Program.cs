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

using var report = new RegressionReport("administration");

var failures = new List<string>();
var path = Path.Combine(Path.GetTempPath(), "aneiang-administration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(path);
var key = Guid.NewGuid().ToString("N");
try
{
    await AuthenticationSecurityChecks.RunAsync(Check, (mode, certificate) => StartHost(mode, certificate: certificate), key);
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

    await using (var host = await StartHost("ApiKey"))
    {
        using var client = Client(host);
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
        await Check("Administration creates clusters and routes through the preserved JSON API", async () =>
        {
            using var cluster = await ReadJson(await client.PutAsJsonAsync("/ops/api/config/clusters/test-cluster", new
            { destinations = new { one = new { address = "http://127.0.0.1:9/" } } }));
            Require(cluster.RootElement.GetProperty("clusterId").GetString() == "test-cluster", "Cluster response changed.");
            using var route = await ReadJson(await client.PutAsJsonAsync("/ops/api/config/routes/test-route", new
            { clusterId = "test-cluster", match = new { path = "/test/{**catch-all}" } }));
            Require(route.RootElement.GetProperty("routeId").GetString() == "test-route", "Route response changed.");
            using var routes = await ReadJson(await client.GetAsync("/ops/api/routes"));
            Require(routes.RootElement.GetProperty("data").GetRawText().Contains("test-route"), "Saved route is missing.");
        });
        await Check("Snapshots, rollback, and import preserve route activation", async () =>
        {
            using var disabled = await ReadJson(await client.PutAsJsonAsync("/ops/api/config/routes/test-route/enabled", new { enabled = false }));
            using var snapshot = await ReadJson(await client.PostAsJsonAsync("/ops/api/config/snapshot", new { description = "Disabled route checkpoint" }));
            var version = snapshot.RootElement.GetProperty("data").GetProperty("versionId").GetString();
            using var exported = await ReadJson(await client.GetAsync("/ops/api/config/export"));
            var saved = exported.RootElement.GetProperty("data");
            Require(!saved.GetProperty("GatewayState").GetProperty("RouteActivation").GetProperty("test-route").GetBoolean(), "Export lost disabled state.");
            using var enabled = await ReadJson(await client.PutAsJsonAsync("/ops/api/config/routes/test-route/enabled", new { enabled = true }));
            using var diff = await ReadJson(await client.GetAsync("/ops/api/config/diff/" + version));
            Require(diff.RootElement.GetProperty("data").GetProperty("activation").GetArrayLength() == 1, "Comparison omitted activation changes.");
            using var rollback = await ReadJson(await client.PostAsync("/ops/api/config/rollback/" + version, null));
            Require(!host.Services.GetRequiredService<Wafish.Services.IDynamicYarpConfigService>().GetDynamicConfig()!.Routes.Single(r => r.Config.RouteId == "test-route").Enabled, "Rollback activated a disabled route.");
            using var enabledAgain = await ReadJson(await client.PutAsJsonAsync("/ops/api/config/routes/test-route/enabled", new { enabled = true }));
            using var imported = await ReadJson(await client.PostAsJsonAsync("/ops/api/config/import", saved));
            Require(!host.Services.GetRequiredService<Wafish.Services.IDynamicYarpConfigService>().GetDynamicConfig()!.Routes.Single(r => r.Config.RouteId == "test-route").Enabled, "Import lost disabled state.");
            using var restored = await ReadJson(await client.PutAsJsonAsync("/ops/api/config/routes/test-route/enabled", new { enabled = true }));
        });

        await Check("Administration exposes storage failures and preserves the active revision", async () =>
        {
            await using var connection = host.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_http_write BEFORE INSERT ON yarp_destinations BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            await command.ExecuteNonQueryAsync();
            try
            {
                using var response = await client.PutAsJsonAsync("/ops/api/config/routes/failed-route", new
                {
                    clusterId = "implicit-cluster", match = new { path = "/failed" },
                    destinations = new { one = "http://127.0.0.1:9/" }
                });
                Require(response.StatusCode == HttpStatusCode.ServiceUnavailable, "Failed write returned success.");
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Require(error.RootElement.GetProperty("status").GetString() == "StorageFailed", "Storage outcome is missing.");
                Require(error.RootElement.GetProperty("activeVersion").GetInt64() == error.RootElement.GetProperty("persistedVersion").GetInt64(), "Failed write changed a revision.");
                var active = host.Services.GetRequiredService<Wafish.Services.IDynamicYarpConfigService>().GetDynamicConfig()!;
                Require(active.Clusters.All(c => c.Config.ClusterId != "implicit-cluster") && active.Routes.All(r => r.Config.RouteId != "failed-route"), "Implicit cluster escaped the transaction.");
            }
            finally
            {
                command.CommandText = "DROP TRIGGER reject_http_write;";
                await command.ExecuteNonQueryAsync();
            }
        });
        await Check("Administration serves system settings and database downloads without Razor", async () =>
        {
            using var system = await ReadJson(await client.GetAsync("/ops/api/settings/system"));
            Require(system.RootElement.GetProperty("data").GetProperty("routePrefix").GetString() == "ops", "Settings prefix changed.");
            Require(system.RootElement.GetProperty("data").GetProperty("locale").GetString() == "en-US", "Unsupported configured locale did not fall back to English.");
            foreach (var url in new[] { "/ops/api/settings/database", "/ops/api/settings/database/backup" })
            {
                using var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                Require(response.Content.Headers.ContentType?.MediaType == "application/octet-stream", "Database response changed.");
                Require((await response.Content.ReadAsByteArrayAsync()).Length > 4096, "Database download was empty.");
            }
        });
        await Check("Encrypted database backup retains committed WAL data and rejects missing keys", async () =>
        {
            await using var source = host.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
            await source.OpenAsync();
            await using var command = source.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE backup_probe (value TEXT); INSERT INTO backup_probe VALUES ('committed-in-wal');";
            await command.ExecuteNonQueryAsync();
            using var response = await client.GetAsync("/ops/api/settings/database/backup");
            response.EnsureSuccessStatusCode();
            Require(response.Content.Headers.ContentType?.MediaType == "application/octet-stream", "Backup was not a database.");
            var backupPath = Path.Combine(path, "downloaded-backup.db");
            await File.WriteAllBytesAsync(backupPath, await response.Content.ReadAsByteArrayAsync());
            var backupSettings = new SqliteConnectionStringBuilder(source.ConnectionString)
            {
                DataSource = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = backupPath }.Uri.AbsoluteUri + "?cipher=sqlcipher&legacy=4",
                Pooling = false
            };
            await using (var backup = new SqliteConnection(backupSettings.ToString()))
            {
                await backup.OpenAsync();
                await using var read = backup.CreateCommand();
                read.CommandText = "SELECT value FROM backup_probe";
                Require(await read.ExecuteScalarAsync() as string == "committed-in-wal", "Backup omitted a committed WAL write.");
                read.CommandText = "PRAGMA integrity_check";
                Require(await read.ExecuteScalarAsync() as string == "ok", "Backup failed integrity validation.");
            }
            backupSettings.Password = "";
            await using var noKey = new SqliteConnection(backupSettings.ToString());
            var rejected = false;
            try
            {
                await noKey.OpenAsync();
                await using var read = noKey.CreateCommand();
                read.CommandText = "SELECT count(*) FROM backup_probe";
                await read.ExecuteScalarAsync();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 26) { rejected = true; }
            Require(rejected, "Encrypted backup was readable without its key.");
        });
        await Check("Administration realtime hubs accept subscriptions and send overview updates", async () =>
        {
            await VerifyHub(client.BaseAddress!, "overview", null, "OverviewUpdate");
            host.Services.GetRequiredService<IProxyLogStore>().Add(new LogEntry { Timestamp = DateTime.Now, EventType = LogEventType.ProxyResponse, RouteId = "test-route", ClusterId = "test-cluster", StatusCode = 200, ElapsedMs = 1 });
            await VerifyHub(client.BaseAddress!, "traffic",
                "{\"type\":1,\"invocationId\":\"1\",\"target\":\"SubscribeToRoutes\",\"arguments\":[[\"test-route\"]]}\u001e", "TrafficUpdate");
        });
        await host.StopAsync();
    }

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
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(path, true);
}

if (failures.Count == 0)
{
    Console.WriteLine("All administration checks passed.");
}
else
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }
}

return failures.Count == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    try { await action(); report.Record(name, started); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { report.Record(name, started, ex); failures.Add(name + ": " + ex.Message); }
}

async Task<WebApplication> StartHost(string mode, bool enableAi = false, string environment = "Development", bool allowAnonymousLoopback = false, bool enableMcp = false, int mcpPort = 8090, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = path });
    builder.Logging.ClearProviders();
    if (certificate == null)
    {
        builder.WebHost.UseUrls("http://127.0.0.1:0");
    }
    else
    {
        builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Loopback, 0);
        options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
    });
    }

    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Gateway:Storage:Sqlite:ConnectionString"] = new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(path, "admin.db"), Password = key }.ToString(),
        ["Gateway:Dashboard:RoutePrefix"] = "ops",
        ["Gateway:Dashboard:Locale"] = "fr-FR",
        ["Gateway:Security:ControlPlane:AllowAnonymousLoopback"] = allowAnonymousLoopback.ToString(),
        ["Gateway:Dashboard:Auth:AuthMode"] = mode,
        ["Gateway:Dashboard:Auth:ApiKey"] = key,
        ["Gateway:Dashboard:Auth:JwtPassword"] = key,
        ["Gateway:Dashboard:Auth:JwtUsername"] = "admin",
        ["Gateway:Dashboard:Auth:TwoFactorStatePath"] = Path.Combine(path, "twofactor.json"),
        ["Gateway:Dashboard:Auth:JwtSecret"] = new string('x', 64),
        ["AI:Enabled"] = "true",
        ["AI:McpServer:Enabled"] = "true",
        ["AI:McpServer:Port"] = mcpPort.ToString()
    });
    builder.Services.AddWafishStorage();
    builder.Services.AddWafishRuntime(enableRegistration: true);
    builder.Services.AddWafishAdministration();
    builder.Services.AddWafishAdministration();
    if (enableAi)
    {
        builder.Services.AddWafishAI();
    }

    if (enableMcp)
    {
        builder.Services.AddWafishMcp();
    }

    var app = builder.Build();
    app.MapWafishAdministration();
    try { await app.StartAsync(); return app; }
    catch { await app.DisposeAsync(); throw; }
}

static HttpClient Client(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
{
    BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
    Timeout = TimeSpan.FromSeconds(15)
};

static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
{
    using (response)
    {
        response.EnsureSuccessStatusCode();
        Require(response.Content.Headers.ContentType?.MediaType == "application/json", "Expected JSON response.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}

async Task VerifyHub(Uri baseAddress, string name, string? invocation, string expected)
{
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("X-Api-Key", key);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    await socket.ConnectAsync(new UriBuilder(baseAddress) { Scheme = "ws", Path = "/ops/hubs/" + name }.Uri, timeout.Token);
    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
    if (invocation != null)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(invocation), WebSocketMessageType.Text, true, timeout.Token);
    }

    var received = new StringBuilder();
    var buffer = new byte[16384];
    while (!received.ToString().Contains(expected, StringComparison.Ordinal))
    {
        var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Require(result.MessageType == WebSocketMessageType.Text, "Hub closed before the expected message.");
        received.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
    Require(!received.ToString().Contains("\"error\"", StringComparison.Ordinal), "Hub returned an error.");
    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Check completed", timeout.Token);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
