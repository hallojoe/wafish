using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wafish.Services;
using Wafish.Storage;
using Yarp.ReverseProxy.Configuration;

internal static class NeedSevenEndpointChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<Action<IServiceCollection>, Task<WebApplication>> start, string credential)
    {
        await run("Administration: WAF previews and all binding mutations expose exclusion audits", async () =>
        {
            await using var host = await start(_ => { });
            using var client = Client(host, credential);
            var config = host.Services.GetRequiredService<DynamicYarpConfigService>();
            var created = await config.ImportBatchAsync([new ClusterConfig { ClusterId = "fields", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://localhost:9000/" } } }],
                [new RouteConfig { RouteId = "fields", ClusterId = "fields", Match = new() { Path = "/fields" } }]);
            Require(created.Success, created.Message);
            var policy = new { pluginId = "waf", scope = "Route", scopeId = "fields", enabled = true, configJson = "{\"exclusions\":[{\"ruleId\":\"sql-injection\",\"target\":\"Json\",\"field\":\"/example\"}]}" };
            using var preview = await client.PostAsJsonAsync("/ops/api/plugin-bindings/preview", policy);
            Require(preview.IsSuccessStatusCode && (await preview.Content.ReadAsStringAsync()).Contains("/example"), await preview.Content.ReadAsStringAsync());
            var repository = host.Services.GetRequiredService<IPluginConfigurationRepository>();
            Require((await repository.GetBindingsAsync()).All(b => b.ScopeId != "fields"), "Preview persisted a binding.");
            using var saved = await client.PostAsJsonAsync("/ops/api/plugin-bindings", policy);
            Require(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var id = (await repository.GetBindingsAsync()).Single(b => b.ScopeId == "fields").Id;
            var current = config.GetDynamicConfig()!;
            var fullPreview = await config.PreviewConfigurationAsync(new(current.Version, current.Routes.Select(r => r.Config).ToArray(), current.Clusters.Select(c => c.Config).ToArray()));
            Require(fullPreview.Valid && fullPreview.WafExclusions["fields"].Single().Field == "/example", "Configuration preview omitted exclusions.");
            using var updated = await client.PutAsJsonAsync("/ops/api/plugin-bindings/" + id, new { policy.pluginId, policy.scope, policy.scopeId, enabled = false, policy.configJson });
            Require(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
            using var enabled = await client.PostAsJsonAsync("/ops/api/plugin-bindings/batch-enabled", new { bindingIds = new[] { id }, enabled = true });
            Require(enabled.IsSuccessStatusCode, await enabled.Content.ReadAsStringAsync());
            using var batch = await client.PostAsJsonAsync("/ops/api/plugin-bindings/batch-create", new { policy.pluginId, policy.scope, scopeIds = new[] { "fields" }, enabled = true, overwriteExisting = true, configJson = "{\"exclusions\":[{\"ruleId\":\"xss\",\"target\":\"Query\",\"field\":\"example\"}]}" });
            Require(batch.IsSuccessStatusCode, await batch.Content.ReadAsStringAsync());
            using var deleted = await client.PostAsJsonAsync("/ops/api/plugin-bindings/batch-delete", new { bindingIds = new[] { id } });
            Require(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
            using var recreated = await client.PostAsJsonAsync("/ops/api/plugin-bindings", policy);
            Require(recreated.IsSuccessStatusCode, await recreated.Content.ReadAsStringAsync());
            id = (await repository.GetBindingsAsync()).Single(b => b.ScopeId == "fields").Id;
            Require((await client.DeleteAsync("/ops/api/plugin-bindings/" + id)).IsSuccessStatusCode, "Delete failed.");
            var audits = host.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent(100).Where(a => a.Action == "WafExclusions").ToArray();
            Require(audits.Length == 7 && audits.All(a => a.Operator == "api-key"), "Exclusion changes lost audit entries or actors: " + audits.Length);
            using var invalid = await client.PostAsJsonAsync("/ops/api/plugin-bindings/preview", new { policy.pluginId, policy.scope, policy.scopeId, enabled = true, configJson = "{\"exclusions\":[{\"ruleId\":\"request-limits\",\"target\":\"Json\",\"field\":\"/*\"}]}" });
            Require(invalid.StatusCode == HttpStatusCode.BadRequest, "Unsafe exclusion preview accepted.");
        });
        await run("Administration: diagnostics, histograms, cache purge, and unsupported backup retain protected contracts", async () =>
        {
            await using var host = await start(s => s.RemoveAll<IStorageBackupProvider>());
            using var client = Client(host, credential);
            foreach (var path in new[] { "/ops/api/plugins/drain", "/ops/api/metrics/latency", "/ops/api/diagnostics/bundle" })
            {
                using var response = await client.GetAsync(path);
                Require(response.IsSuccessStatusCode, path + ": " + await response.Content.ReadAsStringAsync());
                Require(!(await response.Content.ReadAsStringAsync()).Contains(credential), "Diagnostic export leaked credentials.");
            }
            using var purged = await client.PostAsJsonAsync("/ops/api/cache/purge", new { routeId = "fields" });
            Require(purged.IsSuccessStatusCode, await purged.Content.ReadAsStringAsync());
            Require(host.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent().Any(a => a.Action == "CachePurge" && a.Operator == "api-key"), "Purge omitted actor.");
            Require((await client.PostAsJsonAsync("/ops/api/cache/purge", new { })).StatusCode == HttpStatusCode.BadRequest, "Unbounded purge accepted.");
            Require((await client.GetAsync("/ops/api/settings/database/backup")).StatusCode == HttpStatusCode.NotImplemented, "Unsupported backup did not return 501.");
        });
    }
    private static HttpClient Client(WebApplication host, string credential)
    {
        var client = new HttpClient { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Add("X-Api-Key", credential); return client;
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
