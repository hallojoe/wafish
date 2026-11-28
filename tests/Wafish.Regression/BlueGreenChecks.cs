using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class BlueGreenChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Blue-green: health-gated cutover and targeted rollback preserve route identity", async () =>
        {
            await using var blue = Backend("backend-one");
            await using var green = Backend("backend-two");
            await blue.StartAsync(); await green.StartAsync();
            await using var gateway = await RedirectChecks.Fixture.Start();
            using var direct = new HttpClient();
            var route = new { ClusterId = "demo-blue", Match = new { Path = "/blue-green/{**rest}" },
                Transforms = new[] { new { PathRemovePrefix = "/blue-green" } }, Metadata = new { demo = "blue-green" } };
            await Save("clusters/demo-blue", Cluster(blue.Urls.Single()));
            await Save("clusters/demo-green", Cluster(green.Urls.Single()));
            await Save("routes/demo-blue-green", route);
            await Save("routes/demo-unrelated", new { ClusterId = "demo-blue", Match = new { Path = "/unrelated/{**rest}" } });
            await gateway.WaitRoute("demo-blue-green");
            var original = await ReadRoute();
            var uid = original.GetProperty("routeUid").GetString();
            Check(!string.IsNullOrWhiteSpace(uid), "Demo route has no stable identity.");
            await Verify("backend-one", "demo-blue");

            // A failed candidate health check must stop the operator before any route update.
            using (var unhealthy = await direct.GetAsync(green.Urls.Single() + "/unhealthy"))
                Check(!unhealthy.IsSuccessStatusCode, "Unhealthy candidate unexpectedly passed its probe.");
            await Verify("backend-one", "demo-blue");
            using (var healthy = await direct.GetAsync(green.Urls.Single() + "/health")) healthy.EnsureSuccessStatusCode();
            using (var identity = await direct.GetAsync(green.Urls.Single() + "/api/local-service/instance"))
                Check((await identity.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instance").GetString() == "backend-two", "Wrong candidate version.");

            // Export a full native route for targeted rollback, including transforms and metadata.
            using var export = await gateway.Client.GetAsync("/ops/api/config/export"); export.EnsureSuccessStatusCode();
            var saved = (await export.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")
                .GetProperty("ReverseProxy").GetProperty("Routes").GetProperty("demo-blue-green").Clone();
            var candidate = System.Text.Json.Nodes.JsonNode.Parse(saved.GetRawText())!.AsObject();
            candidate["ClusterId"] = "demo-green";
            await Save("routes/demo-blue-green", candidate);
            await Verify("backend-two", "demo-green");
            await Save("routes/demo-blue-green", saved);
            await Verify("backend-one", "demo-blue");
            var restored = await ReadRoute();
            Check(restored.GetProperty("metadata").GetProperty("demo").GetString() == "blue-green", "Rollback lost metadata.");
            using var routes = await gateway.Client.GetAsync("/ops/api/routes");
            Check((await routes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray()
                .Any(r => r.GetProperty("routeId").GetString() == "demo-unrelated"), "Targeted rollback removed an unrelated route.");

            async Task Save(string path, object value)
            {
                using var response = await gateway.Client.PutAsJsonAsync("/ops/api/config/" + path, value, Wafish.Serialization.YarpJsonConfig.Options);
                Check(response.IsSuccessStatusCode, path + ": " + await response.Content.ReadAsStringAsync());
                var mutation = await response.Content.ReadFromJsonAsync<JsonElement>();
                Check(mutation.GetProperty("success").GetBoolean()
                    && mutation.GetProperty("activeVersion").GetInt64() == mutation.GetProperty("persistedVersion").GetInt64(), "Mutation did not activate persisted configuration.");
            }
            async Task<JsonElement> ReadRoute()
            {
                using var response = await gateway.Client.GetAsync("/ops/api/routes"); response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray()
                    .Single(r => r.GetProperty("routeId").GetString() == "demo-blue-green").Clone();
            }
            async Task Verify(string instance, string cluster)
            {
                var current = await ReadRoute();
                Check(current.GetProperty("routeUid").GetString() == uid && current.GetProperty("clusterId").GetString() == cluster,
                    "Cutover or rollback changed route identity or selected the wrong cluster.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    using var response = await gateway.Client.GetAsync("/blue-green/api/local-service/instance", timeout.Token);
                    if (response.IsSuccessStatusCode && (await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("instance").GetString() == instance) break;
                    await Task.Delay(10, timeout.Token);
                }
            }
        });
    }
    private static object Cluster(string address) => new
    {
        Destinations = new { backend = new { Address = address } },
        HealthCheck = new { Active = new { Enabled = true, Interval = "00:00:01", Timeout = "00:00:01", Policy = "ConsecutiveFailures", Path = "/health" } }
    };
    private static WebApplication Backend(string instance)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/unhealthy", () => Results.StatusCode(503));
        app.MapGet("/api/local-service/instance", () => Results.Ok(new { instance }));
        return app;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
