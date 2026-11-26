using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SampleLocalService;
using SampleLocalService.Controllers;

internal static class SampleOrderChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Sample orders: shared durable keys serialize concurrent writes, reject conflicts, expire, and bound storage", LedgerAsync);
        await run("Sample orders: HTTP replay preserves order identity across instances and rejects invalid or unavailable writes", HttpAsync);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task LedgerAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wafish-orders-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "orders.json"); var clock = new Clock();
            var one = new SampleOrderStore(path, clock); var two = new SampleOrderStore(path, clock); var payload = new SampleOrderRequest("demo-product", 2);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingWrites = Enumerable.Range(0, 32).Select(index => Task.Run(async () => { await start.Task; return await (index % 2 == 0 ? one : two).CreateAsync("shared-key", payload, default); })).ToArray();
            start.SetResult(); var results = await Task.WhenAll(pendingWrites);
            Check(results.Count(result => result.Created) == 1 && results.Select(result => result.Order.Id).Distinct().Count() == 1, "Concurrent requests created duplicate orders.");
            var original = results[0].Order;
            Check((await new SampleOrderStore(path, clock).CreateAsync("shared-key", payload, default)).Order == original, "Restart lost the idempotency record.");
            Check(!File.ReadAllText(path).Contains("shared-key", StringComparison.Ordinal), "Raw idempotency key was persisted.");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Ledger permissions are too broad.");
            await Throws<SampleOrderConflictException>(() => one.CreateAsync("shared-key", payload with { Quantity = 3 }, default));
            Check(await two.GetAsync(original.Id, default) == original, "Conflict changed the existing order.");
            using (var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var cancel = new CancellationTokenSource();
                var pending = one.CreateAsync("cancelled-key", payload, cancel.Token);
                Check(!pending.IsCompleted, "A second writer acquired an exclusive lease."); cancel.Cancel();
                await Throws<OperationCanceledException>(async () => await pending);
            }
            using (var cancel = new CancellationTokenSource())
            { cancel.Cancel(); await Throws<OperationCanceledException>(() => one.CreateAsync("pre-cancelled", payload, cancel.Token)); }
            for (var index = 1; index < 256; index++) await one.CreateAsync("key-" + index, payload, default);
            await Throws<SampleOrderCapacityException>(() => two.CreateAsync("capacity", payload, default));
            Check(!(await two.CreateAsync("shared-key", payload, default)).Created, "Full storage rejected a valid replay.");
            clock.Now += TimeSpan.FromMinutes(10);
            Check(await one.GetAsync(original.Id, default) is null, "Expired order was retained.");
            var replacement = await two.CreateAsync("shared-key", payload, default);
            Check(replacement.Created && replacement.Order.Id != original.Id, "Expired key did not release capacity.");
            var saved = await File.ReadAllBytesAsync(path);
            Directory.CreateDirectory(path + ".tmp");
            await Throws<UnauthorizedAccessException>(() => one.CreateAsync("disk-failure", payload, default));
            var afterFailure = await File.ReadAllBytesAsync(path);
            Check(saved.SequenceEqual(afterFailure), "Failed persistence changed committed state."); Directory.Delete(path + ".tmp");
            await File.WriteAllTextAsync(path, "invalid-json");
            await Throws<JsonException>(() => one.CreateAsync("corrupt", payload, default));
            Check(await File.ReadAllTextAsync(path) == "invalid-json", "Corrupt state was replaced silently.");
            await File.WriteAllTextAsync(path, "[]");
            Check(await one.GetAsync(Guid.NewGuid(), default) is null, "Missing order was returned.");
            await File.WriteAllTextAsync(path, "[{\"keyHash\":\"bad\",\"order\":null}]");
            await Throws<IOException>(() => one.GetAsync(Guid.NewGuid(), default));
            await File.WriteAllBytesAsync(path, new byte[1024 * 1024 + 1]);
            await Throws<IOException>(() => one.GetAsync(Guid.NewGuid(), default));
            File.Delete(path); Directory.CreateDirectory(path);
            await Throws<IOException>(() => one.GetAsync(Guid.NewGuid(), default));
            await Throws<ArgumentException>(() => one.CreateAsync("invalid key", payload, default));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task HttpAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wafish-order-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var apps = new List<WebApplication>();
        try
        {
            var path = Path.Combine(directory, "orders.json");
            foreach (var instance in new[] { "orders-one", "orders-two" })
            {
                var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration["Gateway:Registration:InstanceId"] = instance;
                var identity = SampleInstanceIdentity.Resolve(builder.Configuration);
                builder.Services.AddSingleton(identity); builder.Services.AddSingleton(new SampleOrderStore(path, TimeProvider.System));
                builder.Services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
                var app = builder.Build(); apps.Add(app); app.Use(async (context, next) => { identity.Apply(context); await next(context); }); app.MapControllers(); await app.StartAsync();
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpResponseMessage> Post(int instance, string? key, object payload)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, apps[instance].Urls.Single() + "/api/local-service/orders") { Content = JsonContent.Create(payload) };
                if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
                return await client.SendAsync(request);
            }
            var payload = new SampleOrderRequest("demo-product", 2);
            using var first = await Post(0, "http-key", payload); Check(first.StatusCode == HttpStatusCode.Created && first.Headers.Location is not null, "Initial order was not created.");
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync()); var original = firstJson.RootElement.GetProperty("order").GetRawText();
            using var second = await Post(1, "http-key", payload); Check(second.StatusCode == HttpStatusCode.OK, "Replay did not return HTTP 200.");
            using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            Check(secondJson.RootElement.GetProperty("order").GetRawText() == original && secondJson.RootElement.GetProperty("instance").GetString() == "orders-two", "Replay changed the order or backend identity.");
            Check(second.Headers.CacheControl?.NoStore == true && second.Headers.GetValues("X-Backend-Instance").Single() == "orders-two", "Order identity or cache policy was lost.");
            using var get = await client.GetAsync(apps[1].Urls.Single() + first.Headers.Location); Check(get.StatusCode == HttpStatusCode.OK, "Order lookup failed on the other instance.");
            using var missing = await client.GetAsync(apps[0].Urls.Single() + "/api/local-service/orders/" + Guid.NewGuid()); Check(missing.StatusCode == HttpStatusCode.NotFound, "Missing order was not 404.");
            using var conflict = await Post(1, "http-key", payload with { Quantity = 3 }); Check(conflict.StatusCode == HttpStatusCode.Conflict, "Conflicting payload was not rejected.");
            foreach (var key in new string?[] { null, "", "space key", new('k', 65), "one,two" })
            { using var invalid = await Post(0, key, payload); Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid key was accepted."); }
            foreach (var invalidPayload in new object[] { payload with { ProductId = "" }, payload with { ProductId = "private product" }, payload with { Quantity = 0 }, payload with { Quantity = 1001 }, new { productId = "demo", quantity = 1, extra = "unknown" } })
            { using var invalid = await Post(0, "invalid", invalidPayload); Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid payload was accepted."); }
            Directory.CreateDirectory(path + ".tmp");
            using var failedWrite = await Post(0, "write-failure", payload); Check(failedWrite.StatusCode == HttpStatusCode.ServiceUnavailable && failedWrite.Headers.RetryAfter is not null, "Write failure did not return a retry hint.");
            Directory.Delete(path + ".tmp");
            var sharedStore = new SampleOrderStore(path, TimeProvider.System);
            for (var index = 1; index < 256; index++) await sharedStore.CreateAsync("http-capacity-" + index, payload, default);
            using var capacity = await Post(0, "capacity", payload);
            Check(capacity.StatusCode == HttpStatusCode.ServiceUnavailable && capacity.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Capacity did not return a positive retry hint.");
            using var fullReplay = await Post(1, "http-key", payload); Check(fullReplay.StatusCode == HttpStatusCode.OK, "HTTP replay failed at capacity.");
            await File.WriteAllTextAsync(path, "corrupt");
            using var unavailable = await Post(1, "unavailable", payload); Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "Corrupt storage did not fail closed.");
            Check(!(await unavailable.Content.ReadAsStringAsync()).Contains(directory, StringComparison.Ordinal), "Storage error leaked a filesystem path.");
            using var failedRead = await client.GetAsync(apps[0].Urls.Single() + first.Headers.Location); Check(failedRead.StatusCode == HttpStatusCode.ServiceUnavailable, "Read failure was not sanitized.");
        }
        finally { foreach (var app in apps) { await app.StopAsync(); await app.DisposeAsync(); } Directory.Delete(directory, true); }
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
