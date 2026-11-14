using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Plugins;
using Wafish.Storage.Sqlite;

internal static class MutationContractChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<Task<WebApplication>> start, string key)
    {
        await run("Mutation contracts: validation and model binding share Problem Details without losing endpoint fields", async () =>
        {
            await using var host = await start();
            using var client = new HttpClient { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            foreach (var (method, path, payload) in new[]
            {
                (HttpMethod.Put, "/ops/api/config/clusters/contract-invalid", "{}"),
                (HttpMethod.Put, "/ops/api/config/routes/contract-invalid", "{}"),
                (HttpMethod.Post, "/ops/api/config/import", "{}"),
                (HttpMethod.Post, "/ops/api/plugin-bindings", "{}"),
                (HttpMethod.Post, "/ops/api/plugins/install", "{}"),
                (HttpMethod.Post, "/api/gateway/register-route", "{}"),
                (HttpMethod.Put, "/ops/api/config/routes/contract-invalid", "{")
            })
            {
                using var request = new HttpRequestMessage(method, path) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                using var response = await client.SendAsync(request);
                Require(response.StatusCode == HttpStatusCode.BadRequest && response.Content.Headers.ContentType?.MediaType == "application/problem+json", path + " returned inconsistent validation headers.");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Require(body.RootElement.GetProperty("status").GetInt32() == 400 && body.RootElement.GetProperty("outcome").GetString() == "ValidationRejected"
                    && body.RootElement.TryGetProperty("persistedVersion", out _) && body.RootElement.TryGetProperty("activeVersion", out _)
                    && body.RootElement.GetProperty("type").GetString() == "about:blank", path + " omitted the shared error fields.");
            }
            using var saved = await client.PutAsJsonAsync("/ops/api/config/clusters/contract-created", new { destinations = new { one = new { address = "http://localhost:9999/" } } });
            saved.EnsureSuccessStatusCode();
            using var success = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
            Require(success.RootElement.GetProperty("outcome").GetString() == "Applied" && success.RootElement.GetProperty("clusterId").GetString() == "contract-created",
                "Success normalization removed the endpoint identifier.");
            using var registration = await client.PostAsJsonAsync("/api/gateway/register-route", new { routeName = "contract-registration",
                clusterName = "contract-registration", matchPath = "/contract-registration", destinationAddress = "http://localhost:9999/" });
            registration.EnsureSuccessStatusCode();
            using var registrationBody = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
            Require(registrationBody.RootElement.GetProperty("outcome").GetString() == "Applied", "Registration omitted its mutation outcome.");
            using var removed = await client.DeleteAsync("/api/gateway/contract-registration");
            removed.EnsureSuccessStatusCode();
            using var cluster = await client.PostAsJsonAsync("/api/gateway/clusters", new { clusterId = "contract-core", destinations = new { one = "http://localhost:9999/" } });
            cluster.EnsureSuccessStatusCode();
            using var removedCluster = await client.DeleteAsync("/api/gateway/clusters/contract-core");
            removedCluster.EnsureSuccessStatusCode();
            await using var connection = host.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_plugin_contract BEFORE INSERT ON gateway_plugins BEGIN SELECT RAISE(ABORT, 'fixture-secret'); END;";
            await command.ExecuteNonQueryAsync();
            try
            {
                using var rejected = await client.PostAsJsonAsync("/ops/api/plugins/compression/toggle", new { enabled = false });
                using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Require(rejected.StatusCode == HttpStatusCode.ServiceUnavailable && error.RootElement.GetProperty("outcome").GetString() == "StorageFailed"
                    && !error.RootElement.GetProperty("message").GetString()!.Contains("fixture-secret"), "Plugin storage failure lost its classification.");
                Require(host.Services.GetRequiredService<IGatewayPluginManager>().IsPluginEnabled("compression"), "Failed plugin persistence changed enabled state.");
            }
            finally { command.CommandText = "DROP TRIGGER reject_plugin_contract;"; await command.ExecuteNonQueryAsync(); }
            var tools = ActivatorUtilities.CreateInstance<AIFunctionService>(host.Services);
            using var plugins = JsonDocument.Parse(await tools.ExecuteReadToolAsync("list_plugins", null));
            Require(plugins.RootElement.ValueKind == JsonValueKind.Array && plugins.RootElement.GetArrayLength() == host.Services.GetRequiredService<IGatewayPluginManager>().GetAllManifests().Count,
                "AI plugin lookup used an obsolete assembly name.");
            await host.StopAsync();
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
