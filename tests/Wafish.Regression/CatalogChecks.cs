using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SampleDiscoveryCatalog;

internal static class CatalogChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
        => await run("Sample discovery: private bounded catalog controls add, remove, fail, and restore stable endpoints", RunAsync);
    private static async Task RunAsync()
    {
        var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        WebApplication Build(string one = "http://127.0.0.1:19001/", string two = "http://localhost:19002/", string? secret = null, string port = "0")
            => CatalogApplication.Build([], builder =>
            {
                builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Catalog:Backends:backend-one"] = one, ["Catalog:Backends:backend-two"] = two, ["Catalog:ControlKey"] = secret ?? key, ["CATALOG_HTTP_PORT"] = port });
            });
        foreach (var address in new[] { "https://127.0.0.1:19001/", "http://example.com/", "http://127.0.0.1:0/", "http://127.0.0.1/path", "http://user@127.0.0.1/", "invalid" })
        { try { await using var invalid = Build(one: address); throw new Exception("Unsafe catalog origin accepted."); } catch (ArgumentException) { } }
        try { await using var invalid = Build(two: "http://127.0.0.1:19001/"); throw new Exception("Duplicate backend accepted."); } catch (ArgumentException) { }
        try { await using var invalid = Build(secret: "short"); throw new Exception("Missing key accepted."); } catch (ArgumentException) { }
        try { await using var invalid = Build(port: "-1"); throw new Exception("Invalid port accepted."); } catch (ArgumentException) { }
        await using var app = Build(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };
        using (var rejected = await client.PostAsJsonAsync("/__catalog", new CatalogRequest([]))) Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "Unauthenticated catalog update accepted.");
        var original = (await client.GetFromJsonAsync<string[]>("/catalog"))!;
        Check(original.SequenceEqual(["http://127.0.0.1:19001/"]), "Initial catalog changed.");
        client.DefaultRequestHeaders.Add("X-Catalog-Key", key);
        async Task Configure(string[] names, bool unavailable = false)
        { using var response = await client.PostAsJsonAsync("/__catalog", new CatalogRequest(names, unavailable)); response.EnsureSuccessStatusCode(); }
        foreach (var invalid in new string[][] { ["unknown"], ["backend-one", "backend-one"], ["backend-one", "backend-two", "extra"], null! })
        { using var response = await client.PostAsJsonAsync("/__catalog", new CatalogRequest(invalid)); Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid catalog membership accepted."); }
        await Configure(["backend-two", "backend-one"]);
        var both = (await client.GetFromJsonAsync<string[]>("/catalog"))!;
        Check(both.SequenceEqual([original[0], "http://localhost:19002/"]), "Addition changed stable addresses or ordering.");
        await Configure(["backend-two"]); Check((await client.GetFromJsonAsync<string[]>("/catalog"))!.SequenceEqual([both[1]]), "Removal retained an endpoint.");
        await Configure(["backend-one"], true);
        using (var failed = await client.GetAsync("/catalog")) Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable, "Unavailable catalog still returned endpoints.");
        var health = await client.GetFromJsonAsync<JsonElement>("/__catalog"); Check(health.GetProperty("unavailable").GetBoolean() && health.GetProperty("requests").GetInt64() == 4, "Failure was not visible.");
        await Configure(["backend-one"]); Check((await client.GetFromJsonAsync<string[]>("/catalog"))!.SequenceEqual(original), "Recovery changed endpoint identity.");
        await Configure([]); Check((await client.GetFromJsonAsync<string[]>("/catalog"))!.Length == 0, "Empty catalog not supported.");
        using (var response = await client.GetAsync("/catalog")) Check(response.Headers.CacheControl?.NoStore == true, "Catalog can be cached.");
        using (var response = await client.GetAsync("/health")) response.EnsureSuccessStatusCode();
        await app.StopAsync();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
