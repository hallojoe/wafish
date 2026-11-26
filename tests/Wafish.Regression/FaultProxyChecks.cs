using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SampleFaultProxy;

internal static class FaultProxyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
        => await run("Sample network faults: bounded resets, delays, cancellation, and private controls preserve real forwarding", RunAsync);

    private static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var backend = builder.Build(); backend.MapGet("/api/local-service/instance", () => Results.Text("backend-one"));
        await backend.StartAsync();
        var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        WebApplication Proxy(string target, string secret, string port = "0") => FaultProxyApplication.Build([], options =>
        {
            options.Logging.ClearProviders();
            options.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Fault:Target"] = target, ["Fault:ControlKey"] = secret, ["FAULT_HTTP_PORT"] = port });
        });
        foreach (var target in new[] { "https://127.0.0.1:123/", "http://example.com/", "http://127.0.0.1:0/", "http://127.0.0.1/path", "http://user@127.0.0.1/", "invalid" })
        { try { await using var invalid = Proxy(target, key); throw new Exception("Unsafe target accepted."); } catch (ArgumentException) { } }
        try { await using var invalid = Proxy(backend.Urls.Single(), "short"); throw new Exception("Missing key accepted."); } catch (ArgumentException) { }
        try { await using var invalid = Proxy(backend.Urls.Single(), key, "65536"); throw new Exception("Invalid port accepted."); } catch (ArgumentException) { }
        await using var proxy = Proxy(backend.Urls.Single(), key); await proxy.StartAsync();
        using var client = new HttpClient { BaseAddress = new(proxy.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        using (var rejected = await client.GetAsync("/__fault")) Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "Controls accepted unauthenticated access.");
        client.DefaultRequestHeaders.Add("X-Fault-Key", key);
        async Task<JsonElement> State() => await client.GetFromJsonAsync<JsonElement>("/__fault");
        async Task Configure(string mode, int count, int delay = 0)
        { using var response = await client.PostAsJsonAsync("/__fault", new FaultRequest(mode, count, delay)); response.EnsureSuccessStatusCode(); }
        foreach (var invalid in new[] { new FaultRequest("bad"), new("reset", 0), new("reset", 101), new("delay", 1, 0), new("delay", 1, 5001), new("none", -1) })
        { using var response = await client.PostAsJsonAsync("/__fault", invalid); Check(response.StatusCode == HttpStatusCode.BadRequest, "Unbounded fault accepted."); }
        Check(await client.GetStringAsync("/api/local-service/instance") == "backend-one", "Initial forwarding failed.");
        await Configure("reset", 1);
        try { await client.GetStringAsync("/api/local-service/instance"); throw new Exception("Reset did not interrupt transport."); } catch (HttpRequestException) { }
        Check(await client.GetStringAsync("/api/local-service/instance") == "backend-one", "Reset count did not expire.");
        var timer = Stopwatch.StartNew(); await Configure("delay", 1, 100);
        Check(await client.GetStringAsync("/api/local-service/instance") == "backend-one" && timer.ElapsedMilliseconds >= 90, "Delay did not affect forwarding.");
        await Configure("delay", 1, 5000);
        using var cancellation = new CancellationTokenSource();
        var delayed = client.GetAsync("/api/local-service/instance", cancellation.Token);
        await Until(async () => (await State()).GetProperty("active").GetInt32() == 1);
        cancellation.Cancel(); try { using var unexpected = await delayed; throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        await Until(async () => (await State()).GetProperty("active").GetInt32() == 0);
        await Configure("none", 0);
        var state = await State(); Check(state.GetProperty("resets").GetInt64() == 1 && state.GetProperty("delays").GetInt64() == 2, "Fault accounting changed.");
        using (var health = await client.GetAsync("/health")) health.EnsureSuccessStatusCode();
        using (var response = await client.GetAsync("/__fault")) Check(response.Headers.CacheControl?.NoStore == true, "Controls can be cached.");
        var concurrent = new FaultState(); Check(concurrent.Configure(new("reset", 100)), "Cannot configure bounded state.");
        var resets = 0;
        Parallel.For(0, 200, _ => { if (concurrent.Take().Mode == "reset") Interlocked.Increment(ref resets); concurrent.Finish(); });
        Check(resets == 100, "Concurrent faults exceeded their configured count.");
        await proxy.StopAsync(); await backend.StopAsync();
    }
    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate()) await Task.Delay(10, deadline.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
