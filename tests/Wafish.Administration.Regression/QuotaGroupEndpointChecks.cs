using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Services;
using Wafish.Storage;
using Yarp.ReverseProxy.Configuration;

internal static class QuotaGroupEndpointChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<Task<WebApplication>> start, string credential)
    {
        await run("Administration: quota-group previews and mutations reject conflicts without changing state", async () =>
        {
            await using var host = await start();
            using var client = new HttpClient { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            client.DefaultRequestHeaders.Add("X-Api-Key", credential);
            var config = host.Services.GetRequiredService<DynamicYarpConfigService>();
            Require((await config.ImportBatchAsync([new ClusterConfig { ClusterId = "quota-api", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://127.0.0.1:9/" } } }],
                new[] { "quota-a", "quota-b" }.Select(id => new RouteConfig { RouteId = id, ClusterId = "quota-api", Match = new() { Path = "/" + id } }).ToArray())).Success, "Fixture routes failed.");
            object Policy(string route, int limit) => new { pluginId = "rate-limit", scope = "Route", scopeId = route, enabled = true,
                configJson = JsonSerializer.Serialize(new { enabled = true, quotaGroup = "quota-api", permitLimit = limit, window = "1m", partitionKey = "Global" }) };
            var repository = host.Services.GetRequiredService<IPluginConfigurationRepository>();
            using var first = await client.PostAsJsonAsync("/ops/api/plugin-bindings", Policy("quota-a", 2));
            Require(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
            var before = await repository.GetBindingsSnapshotAsync();
            var publisher = host.Services.GetRequiredService<IGatewaySnapshotPublisher>(); var version = publisher.Current.Version;
            foreach (var path in new[] { "/ops/api/plugin-bindings/preview", "/ops/api/plugin-bindings" })
            {
                using var rejected = await client.PostAsJsonAsync(path, Policy("quota-b", 3));
                using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Require(rejected.StatusCode == HttpStatusCode.BadRequest && body.RootElement.GetProperty("outcome").GetString() == "ValidationRejected", "Conflict lost HTTP validation semantics.");
            }
            var after = await repository.GetBindingsSnapshotAsync();
            Require(before.Revision == after.Revision && publisher.Current.Version == version, "Rejected API operations changed state.");
            using var second = await client.PostAsJsonAsync("/ops/api/plugin-bindings", Policy("quota-b", 2));
            Require(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
            var id = (await repository.GetBindingsAsync()).Single(b => b.ScopeId == "quota-a" && b.PluginId == "rate-limit").Id;
            before = await repository.GetBindingsSnapshotAsync(); version = publisher.Current.Version;
            using var update = await client.PutAsJsonAsync("/ops/api/plugin-bindings/" + id, Policy("quota-a", 3));
            Require(update.StatusCode == HttpStatusCode.BadRequest, "Conflicting update was accepted.");
            using var batch = await client.PostAsJsonAsync("/ops/api/plugin-bindings/batch-create", new { pluginId = "rate-limit", scope = "Route", scopeIds = new[] { "quota-b" }, enabled = true,
                overwriteExisting = true, configJson = JsonSerializer.Serialize(new { enabled = true, quotaGroup = "quota-api", permitLimit = 3, window = "1m", partitionKey = "Global" }) });
            Require(batch.StatusCode == HttpStatusCode.BadRequest, "Conflicting batch was accepted.");
            using var validPreview = await client.PostAsJsonAsync("/ops/api/plugin-bindings/preview", Policy("quota-a", 2));
            Require(validPreview.IsSuccessStatusCode, "Replacement preview duplicated its existing binding.");
            after = await repository.GetBindingsSnapshotAsync();
            Require(before.Revision == after.Revision && publisher.Current.Version == version, "Updates or preview changed state after rejection.");
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
