using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Services;

internal static class PreviewEndpointChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<Task<WebApplication>> start, string credential)
    {
        await run("Administration: configuration preview and idempotent apply preserve HTTP results", async () =>
        {
            await using var host = await start();
            using var client = Client(host, credential);
            var version = host.Services.GetRequiredService<IDynamicYarpConfigService>().GetDynamicConfig()!.Version;
            var request = new { expectedVersion = version, routes = Array.Empty<object>(), clusters = Array.Empty<object>() };
            using var preview = await client.PostAsJsonAsync("/ops/api/config/changes/preview", request);
            Require(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            Require(host.Services.GetRequiredService<IDynamicYarpConfigService>().GetDynamicConfig()!.Version == version, "HTTP preview mutated configuration.");
            using var noKey = await client.PostAsJsonAsync("/ops/api/config/changes", request);
            Require(noKey.StatusCode == HttpStatusCode.BadRequest, "Missing idempotency key accepted.");
            client.DefaultRequestHeaders.Add("Idempotency-Key", "endpoint-test");
            using var applied = await client.PostAsJsonAsync("/ops/api/config/changes", request);
            Require(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
            var original = await applied.Content.ReadAsStringAsync();
            using var replay = await client.PostAsJsonAsync("/ops/api/config/changes", request);
            Require(await replay.Content.ReadAsStringAsync() == original, "HTTP retry changed its acknowledgement.");
            using var conflict = await client.PostAsJsonAsync("/ops/api/config/changes", new { expectedVersion = version + 1, routes = Array.Empty<object>(), clusters = Array.Empty<object>() });
            Require(conflict.StatusCode == HttpStatusCode.Conflict, "Changed key payload did not conflict.");
            using var stale = await client.PostAsJsonAsync("/ops/api/config/changes/preview", request);
            Require(stale.StatusCode == HttpStatusCode.Conflict, "Stale preview did not conflict.");
            using var invalid = await client.PostAsJsonAsync("/ops/api/config/changes/preview", new { expectedVersion = version + 1, routes = new[] { new { routeId = "bad", clusterId = "missing", match = new { path = "/bad" } } }, clusters = Array.Empty<object>() });
            Require(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid preview did not return 400.");
        });
        await run("Administration: plugin preview reports health without activating a candidate", async () =>
        {
            await using var host = await start();
            using var client = Client(host, credential);
            using var preview = await client.PostAsJsonAsync("/ops/api/plugins/preview", new { enabledPluginIds = new[] { "compression" } });
            Require(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Require(body.RootElement.GetProperty("plugins").GetArrayLength() == 1, "Plugin preview omitted its report.");
            using var missing = await client.PostAsJsonAsync("/ops/api/plugins/preview", new { enabledPluginIds = new[] { "missing" } });
            Require(missing.StatusCode == HttpStatusCode.BadRequest, "Missing plugin accepted.");
            using var absent = await client.PostAsJsonAsync("/ops/api/plugins/preview", new { });
            Require(absent.StatusCode == HttpStatusCode.BadRequest, "Missing plugin list accepted.");
        });
    }
    private static HttpClient Client(WebApplication host, string credential)
    {
        var client = new HttpClient { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Add("X-Api-Key", credential);
        return client;
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
