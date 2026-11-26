using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using SampleLocalService;
using SampleLocalService.Controllers;
using Wafish.Runtime;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Wafish.Storage.Sqlite;

internal static class SampleStreamingChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
        => await run("Sample streaming: early events, cancellation, bounds, cache bypass, and identity survive active gateway plugins", StreamingAsync);
    private static async Task StreamingAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wafish-sample-stream-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var backendBuilder = WebApplication.CreateBuilder(); backendBuilder.Logging.ClearProviders(); backendBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            backendBuilder.Configuration["Gateway:Registration:InstanceId"] = "stream-backend";
            var identity = SampleInstanceIdentity.Resolve(backendBuilder.Configuration); var activity = new SampleStreamActivity();
            backendBuilder.Services.AddSingleton(identity); backendBuilder.Services.AddSingleton(activity);
            backendBuilder.Services.AddControllers().AddApplicationPart(typeof(StreamingController).Assembly);
            await using var backend = backendBuilder.Build(); backend.Use(async (context, next) => { identity.Apply(context); await next(context); }); backend.MapControllers(); await backend.StartAsync();
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Storage:Sqlite:ConnectionString"] = "Data Source=" + Path.Combine(directory, "gateway.db"),
                ["Gateway:Deployment:Mode"] = "ProxyOnly",
                ["ReverseProxy:Routes:stream:ClusterId"] = "sample", ["ReverseProxy:Routes:stream:Match:Path"] = "/api/local-service/{**rest}",
                ["ReverseProxy:Clusters:sample:Destinations:one:Address"] = backend.Urls.Single()
            });
            builder.Services.AddWafishStorage(); builder.Services.AddWafishRuntime();
            await using var gateway = builder.Build();
            foreach (var plugin in new[] { "response-cache", "compression", "request-retry" })
                await gateway.Services.GetRequiredService<IPluginConfigurationRepository>().UpsertBindingAsync(new PluginBindingEntity
                { PluginId = plugin, PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "stream", ConfigJson = "{\"enabled\":true}" });
            gateway.UseRouting(); gateway.UseWafishGateway(); await gateway.StartAsync();
            using var client = new HttpClient { BaseAddress = new(gateway.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) }; client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
            foreach (var query in new[] { "count=0", "count=65", "intervalMs=-1", "intervalMs=1001", "count=64&intervalMs=1000" })
            { using var invalid = await client.GetAsync("/api/local-service/stream?" + query); Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Unbounded stream accepted."); }
            Check(activity.Snapshot().Started == 0, "Rejected stream started work.");
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                using var response = await client.GetAsync("/api/local-service/stream?count=20&intervalMs=250", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                response.EnsureSuccessStatusCode();
                Check(response.Content.Headers.ContentType?.MediaType == "text/event-stream" && response.Content.Headers.ContentEncoding.Count == 0 && response.Headers.CacheControl?.NoStore == true, "Streaming response policy changed.");
                Check(response.Headers.GetValues("X-Backend-Instance").Single() == "stream-backend", "Streaming response lost instance identity.");
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
                Check(await reader.ReadLineAsync(cancellation.Token) == "id: 0", "Initial event framing changed.");
                var first = await reader.ReadLineAsync(cancellation.Token); Check(first?.StartsWith("data: ", StringComparison.Ordinal) == true, "First event missing.");
                using var data = JsonDocument.Parse(first![6..]); Check(data.RootElement.GetProperty("sequence").GetInt32() == 0, "Synthetic sequence changed.");
                Check(activity.Snapshot().Active == 1 && activity.Snapshot().Completed == 0, "Gateway buffered the first event until completion.");
                cancellation.Cancel();
            }
            await Until(() => activity.Snapshot().Active == 0);
            var cancelled = activity.Snapshot(); Check(cancelled.Started == 1 && cancelled.Cancelled == 1 && cancelled.Events < 20, "Disconnect did not stop backend work or was retried.");
            var firstBody = await client.GetStringAsync("/api/local-service/stream?count=3&intervalMs=10");
            var secondBody = await client.GetStringAsync("/api/local-service/stream?count=3&intervalMs=10");
            Check(firstBody.Split("data: ").Length == 4 && secondBody.Split("data: ").Length == 4 && firstBody != secondBody, "Stream was truncated or replayed from cache.");
            Check(activity.Snapshot().Started == 3 && activity.Snapshot().Completed == 2, "Plugins retried or cached completed streams.");
            for (var index = 0; index < 16; index++) Check(activity.TryStart(), "Cannot reserve bounded synthetic work.");
            using (var full = await client.GetAsync("/api/local-service/stream")) Check(full.StatusCode == HttpStatusCode.TooManyRequests && full.Headers.RetryAfter is not null, "Active stream capacity was not enforced.");
            for (var index = 0; index < 16; index++) activity.Finish(false);
            Check(activity.Snapshot().Active == 0, "Synthetic capacity was not released.");
            await gateway.StopAsync(); await backend.StopAsync();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
