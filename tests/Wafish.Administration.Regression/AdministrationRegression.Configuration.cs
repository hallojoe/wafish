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
    private async Task CheckStorageBoundsAsync()
    {
        await Check("Storage API: oversized filters return 400 and extreme pages do not wrap", async () =>
        {
            await using var host = await StartHost("ApiKey");
            using var client = Client(host);
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            using var rejected = await client.GetAsync("/ops/api/logs/history?keyword=" + new string('x', 1025));
            Require(rejected.StatusCode == HttpStatusCode.BadRequest, "Oversized filter did not return 400.");
            using var history = await ReadJson(await client.GetAsync("/ops/api/logs/history?page=2147483647&pageSize=2147483647"));
            var data = history.RootElement.GetProperty("data");
            Require(data.GetProperty("pageSize").GetInt32() == 200 && data.GetProperty("items").GetArrayLength() == 0, "Historical page bounds changed.");
            host.Services.GetRequiredService<ProxyLogStore>().Add(new() { Timestamp = DateTime.UtcNow, EventType = LogEventType.ProxyResponse });
            using var recent = await ReadJson(await client.GetAsync("/ops/api/logs?page=2147483647&pageSize=2147483647"));
            Require(recent.RootElement.GetProperty("data").GetProperty("entries").GetArrayLength() == 0, "Recent page offset overflowed.");
            await host.StopAsync();
        });
    }

    private async Task CheckConfigurationAsync(WebApplication host, HttpClient client)
    {
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
                    Require(error.RootElement.GetProperty("outcome").GetString() == "StorageFailed", "Storage outcome is missing.");
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
    }

}
