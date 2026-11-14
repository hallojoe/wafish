using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Services;
using Wafish.Storage;

internal static class NeedSixEndpointChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<Action<IServiceCollection>?, Task<WebApplication>> start, string key)
    {
        await run("Change set API: preview requires read access and apply requires configuration and plugin permissions", async () =>
        {
            await using var host = await start(null);
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            var state = host.Services.GetRequiredService<IDynamicYarpConfigService>().GetDynamicConfig()!;
            var bindings = await host.Services.GetRequiredService<IPluginConfigurationRepository>().GetBindingsSnapshotAsync();
            var request = new ConfigurationChangeSetRequest(new(state.Version, state.Routes.Select(r => r.Config).ToArray(), state.Clusters.Select(c => c.Config).ToArray()), bindings.Revision, bindings.Bindings);
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets/preview", request)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous preview succeeded.");
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            client.DefaultRequestHeaders.Add("Idempotency-Key", "combined-api");
            var auth = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
            auth.Permissions = [DashboardPermission.Read];
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets/preview", request)).IsSuccessStatusCode, "Reader preview failed.");
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.Forbidden, "Reader applied changes.");
            auth.Permissions = [DashboardPermission.ConfigurationWrite];
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.Forbidden, "Configuration permission bypassed plugin permission.");
            auth.Permissions = [DashboardPermission.PluginManagement];
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.Forbidden, "Plugin permission bypassed configuration permission.");
            auth.Permissions = [DashboardPermission.ConfigurationWrite, DashboardPermission.PluginManagement];
            using var response = await client.PostAsJsonAsync("/ops/api/config/change-sets", request);
            Check(response.IsSuccessStatusCode, "Authorized combined apply failed: " + await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<ConfigurationChangeSetResult>();
            Check(result!.Configuration.Success && !string.IsNullOrWhiteSpace(result.Actor), "Apply omitted its actor or outcome.");
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).IsSuccessStatusCode, "Retry lost the receipt.");
            auth.Permissions = [DashboardPermission.Read, DashboardPermission.ConfigurationWrite, DashboardPermission.PluginManagement];
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets/preview", request)).StatusCode == HttpStatusCode.Conflict, "Stale preview did not report conflict.");
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.BadRequest, "Missing receipt key accepted.");
            client.DefaultRequestHeaders.Add("Idempotency-Key", "combined-api-stale");
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.Conflict, "Stale apply did not report conflict.");
            var current = host.Services.GetRequiredService<IDynamicYarpConfigService>().GetDynamicConfig()!;
            var fresh = request with { Configuration = request.Configuration with { ExpectedVersion = current.Version } };
            var invalid = fresh with { Configuration = fresh.Configuration with { Routes = [new() { RouteId = "invalid", ClusterId = "missing", Match = new() { Path = "/invalid" } }] } };
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", invalid)).StatusCode == HttpStatusCode.BadRequest, "Invalid combined configuration did not return 400.");
            await using var connection = host.Services.GetRequiredService<Wafish.Storage.Sqlite.SqliteConnectionFactory>().CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_combined_api BEFORE UPDATE ON configuration_version BEGIN SELECT RAISE(ABORT, 'injected'); END;";
            await command.ExecuteNonQueryAsync();
            try
            {
                Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", fresh)).StatusCode == HttpStatusCode.InternalServerError, "Storage failure reported success.");
            }
            finally { command.CommandText = "DROP TRIGGER reject_combined_api;"; await command.ExecuteNonQueryAsync(); }

        });
        await run("Maintenance API: authorization, actor, reason, expiry, and cancellation are enforced", async () =>
        {
            await using var host = await start(null);
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            var config = host.Services.GetRequiredService<IDynamicYarpConfigService>();
            await config.TryAddClusterConfig(new Yarp.ReverseProxy.Configuration.ClusterConfig
            { ClusterId = "maintenance-test", Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig> { ["one"] = new() { Address = "http://127.0.0.1:9/" } } });
            var request = new { clusterId = "maintenance-test", destinationId = "one", reason = "repair", durationSeconds = 60, allowRecoveryProbes = true };
            Check((await client.PutAsJsonAsync("/ops/api/circuit-breaker/maintenance", request)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous maintenance succeeded.");
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            var auth = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
            auth.Permissions = [DashboardPermission.Read];
            Check((await client.PutAsJsonAsync("/ops/api/circuit-breaker/maintenance", request)).StatusCode == HttpStatusCode.Forbidden, "Reader changed maintenance.");
            auth.Permissions = [DashboardPermission.Read, DashboardPermission.PluginManagement];
            using var response = await client.PutAsJsonAsync("/ops/api/circuit-breaker/maintenance", request);
            Check(response.IsSuccessStatusCode, "Authorized maintenance failed.");
            var window = await response.Content.ReadFromJsonAsync<Wafish.Infrastructure.State.DestinationMaintenanceWindow>();
            Check(window!.Reason == "repair" && window.AllowRecoveryProbes && window.ExpiresAt > window.StartedAt && !string.IsNullOrWhiteSpace(window.Actor), "Maintenance metadata was lost.");
            Check((await client.GetFromJsonAsync<Wafish.Infrastructure.State.DestinationMaintenanceWindow[]>("/ops/api/circuit-breaker/maintenance"))!.Length == 1, "Maintenance listing omitted the window.");
            Check((await client.PutAsJsonAsync("/ops/api/circuit-breaker/maintenance", new { clusterId = "maintenance-test", destinationId = "one", reason = "repair", durationSeconds = 0 })).StatusCode == HttpStatusCode.BadRequest, "Invalid duration accepted.");
            Check((await client.PutAsJsonAsync("/ops/api/circuit-breaker/maintenance", new { clusterId = "maintenance-test", destinationId = "missing", reason = "repair", durationSeconds = 60 })).StatusCode == HttpStatusCode.NotFound, "Missing destination accepted.");
            Check((await client.DeleteAsync("/ops/api/circuit-breaker/maintenance?clusterId=maintenance-test&destinationId=one")).StatusCode == HttpStatusCode.NoContent, "Maintenance cancellation failed.");
            Check((await client.DeleteAsync("/ops/api/circuit-breaker/maintenance?clusterId=maintenance-test&destinationId=one")).StatusCode == HttpStatusCode.NotFound, "Missing cancellation reported success.");
        });

    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
