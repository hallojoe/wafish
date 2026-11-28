using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SampleDiscoveryCatalog;

internal static class DiscoveryCatalogChecks
{
    private static Dictionary<string, string> Backends() => new() { ["backend-one"] = "http://127.0.0.1:5101/", ["backend-two"] = "http://127.0.0.1:5102/" };
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Discovery sample: bounded configured origins and atomic selections reject untrusted additions", () =>
        {
            foreach (var invalid in new Dictionary<string, string>[] { [], Enumerable.Range(0, 9).ToDictionary(i => "backend-" + i, i => "http://127.0.0.1:" + (5100 + i)),
                new() { ["Upper"] = "http://localhost:1" }, new() { ["../escape"] = "http://localhost:1" }, new() { [new string('a', 41)] = "http://localhost:1" },
                new() { ["one"] = "http://localhost:1", ["two"] = "http://localhost:1/" } })
                Reject(() => new DiscoveryCatalog(invalid));
            foreach (var address in new[] { "bad", "https://localhost:1", "http://example.test", "http://name:secret@localhost", "http://localhost/path", "http://localhost?secret=value", "http://localhost#fragment" })
                Reject(() => new DiscoveryCatalog(new Dictionary<string, string> { ["one"] = address }));
            var source = Backends(); var catalog = new DiscoveryCatalog(source); source["backend-one"] = "http://outside.test";
            Check(catalog.ReadCatalog().Catalog.Endpoints[0] == "http://127.0.0.1:5101/", "Catalog retained mutable input configuration.");
            foreach (var invalid in new CatalogSelectionRequest[] { new(null, []), new("unknown", []), new("healthy", null), new("healthy", [null!]),
                new("healthy", ["missing"]), new("healthy", ["backend-one", "backend-one"]), new("healthy", Enumerable.Repeat("backend-one", 9).ToArray()) })
                Check(!catalog.TryUpdate(invalid) && catalog.ReadSelection().Backends.Length == 2, "Invalid selection changed state.");
            var copy = catalog.ReadSelection(); copy.Backends[0] = "outside";
            var endpoints = catalog.ReadCatalog(); endpoints.Catalog.Endpoints[0] = "http://outside.test";
            Check(catalog.ReadSelection().Backends[0] == "backend-one" && catalog.ReadCatalog().Catalog.Endpoints[0] == "http://127.0.0.1:5101/", "Catalog exposed mutable state.");
            Parallel.For(0, 1000, i =>
            {
                Check(catalog.TryUpdate(new(i % 2 == 0 ? "healthy" : "malformed", i % 2 == 0 ? ["backend-one"] : ["backend-two"])), "Configured selection failed.");
                var snapshot = catalog.ReadCatalog();
                Check(snapshot.Catalog.Endpoints.Single() == (snapshot.Mode == "healthy" ? "http://127.0.0.1:5101/" : "http://127.0.0.1:5102/"), "Concurrent read mixed selection revisions.");
            });
            Check(catalog.TryUpdate(new("unavailable", [])) && catalog.ReadCatalog().Catalog.Endpoints.Length == 0, "Empty configured catalog was rejected.");
            return Task.CompletedTask;
        });
        await run("Discovery sample: HTTP controls expose bounded catalogs, explicit failures, validation, and OpenAPI", async () =>
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(DiscoveryCatalog).Assembly.GetName().Name });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
            builder.Services.AddDiscoveryCatalog(new DiscoveryCatalog(Backends()));
            await using var app = builder.Build(); app.UseExceptionHandler(); app.UseStatusCodePages(); app.MapDiscoveryCatalog(); app.MapOpenApi();
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
                Check((await client.GetFromJsonAsync<CatalogResponse>("/catalog"))!.Endpoints.Length == 2, "Initial catalog omitted configured destinations.");
                foreach (var mode in new[] { "healthy", "unavailable", "malformed" })
                {
                    using var change = await client.PutAsJsonAsync("/control", new CatalogSelectionRequest(mode, ["backend-two"]));
                    Check(change.StatusCode == HttpStatusCode.NoContent, "Selection mutation failed.");
                    var selected = (await client.GetFromJsonAsync<CatalogSelectionResponse>("/control"))!;
                    Check(selected.Mode == mode && selected.Backends.SequenceEqual(["backend-two"]), "Control response lost selection.");
                    using var response = await client.GetAsync("/catalog");
                    if (mode == "healthy") Check((await response.Content.ReadFromJsonAsync<CatalogResponse>())!.Endpoints.SequenceEqual(["http://127.0.0.1:5102/"]), "Catalog did not remove a backend.");
                    else if (mode == "unavailable") Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "Outage mode returned success.");
                    else Check(response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "{", "Malformed mode unexpectedly returned valid JSON.");
                }
                using (var invalid = await client.PutAsJsonAsync("/control", new CatalogSelectionRequest("healthy", ["unconfigured"])))
                    Check(invalid.StatusCode == HttpStatusCode.BadRequest && invalid.Content.Headers.ContentType?.MediaType == "application/problem+json", "Invalid controls did not return validation details.");
                Check((await client.GetFromJsonAsync<CatalogSelectionResponse>("/control"))!.Mode == "malformed", "Invalid control request changed mode.");
                using (var malformed = await client.PutAsync("/control", new StringContent("{", Encoding.UTF8, "application/json"))) Check(malformed.StatusCode == HttpStatusCode.BadRequest, "Malformed control JSON accepted.");
                using (var oversized = await client.PutAsync("/control", new StringContent(new string(' ', 5000) + "{}", Encoding.UTF8, "application/json"))) Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Unbounded control body accepted.");
                using (var empty = await client.PutAsJsonAsync("/control", new CatalogSelectionRequest("healthy", []))) Check(empty.StatusCode == HttpStatusCode.NoContent, "Empty selection failed.");
                Check((await client.GetFromJsonAsync<CatalogResponse>("/catalog"))!.Endpoints.Length == 0, "Empty selection did not remove destinations.");
                var openapi = (await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json")).GetProperty("paths");
                Check(openapi.GetProperty("/catalog").TryGetProperty("get", out _) && openapi.GetProperty("/control").TryGetProperty("get", out _) && openapi.GetProperty("/control").TryGetProperty("put", out _), "OpenAPI omitted catalog operations.");
            }
            finally { await app.StopAsync(); }
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid catalog configuration accepted."); }
}
