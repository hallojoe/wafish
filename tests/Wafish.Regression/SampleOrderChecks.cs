using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SampleLocalService;
using SampleLocalService.Controllers;
using SampleLocalService.Services;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

internal static class SampleOrderChecks
{
    private const string Path = "/api/local-service/orders";
    private const string Key = "sample-key-00000001";
    private static readonly CreateSampleOrderRequest Order = new() { Product = "sample-widget", Quantity = 2 };

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Sample orders: atomic duplicate handling preserves one result and rejects conflicting payloads", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime(); var service = new SampleOrderService(clock, new("one"));
            using var barrier = new ManualResetEventSlim();
            var calls = Enumerable.Range(0, 64).Select(_ => Task.Run(() => { barrier.Wait(); return service.Create(Key, Order, default); })).ToArray();
            barrier.Set(); var results = await Task.WhenAll(calls);
            Check(results.Count(item => item.Outcome == SampleOrderOutcome.Created) == 1 && results.Count(item => item.Outcome == SampleOrderOutcome.Replayed) == 63, "Concurrent duplicates committed more than once.");
            Check(results.Select(item => item.Order).Distinct().Count() == 1, "Duplicate results differ.");
            var initial = results[0].Order!;
            foreach (var conflict in new[] { Order with { Quantity = 3 }, Order with { Product = "sample-gadget" } })
                Check(service.Create(Key, conflict, default).Outcome == SampleOrderOutcome.Conflict, "Conflicting payload reused a key.");
            Check(service.Get(initial.Id, default) == initial, "Conflicts mutated the original order.");
            Check(service.Create(Key.ToUpperInvariant(), Order, default).Outcome == SampleOrderOutcome.Created, "Key matching lost ordinal case sensitivity.");
            Check(new SampleOrderService(clock, new("two")).Create(Key, Order, default).Order!.Id != initial.Id, "Independent processes unexpectedly share idempotency state.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { service.Create("cancelled-key-0001", Order, cancelled.Token); throw new Exception("Cancelled creation succeeded."); } catch (OperationCanceledException) { }
            Check(service.Create("cancelled-key-0001", Order, default).Outcome == SampleOrderOutcome.Created, "Cancelled request consumed its key.");
            try { service.Get(initial.Id, cancelled.Token); throw new Exception("Cancelled read succeeded."); } catch (OperationCanceledException) { }
        });
        await run("Sample orders: capacity preserves retained keys and monotonic expiry permits later reuse", () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime(); var service = new SampleOrderService(clock, new("one"));
            var initial = service.Create(Key, Order, default).Order!;
            for (var i = 1; i < SampleOrderService.Capacity; i++) Check(service.Create($"capacity-{i:D12}", Order, default).Outcome == SampleOrderOutcome.Created, "Capacity was reached early.");
            Check(service.Create("overflow-key-0001", Order, default) is { Outcome: SampleOrderOutcome.Capacity, RetryAfterSeconds: 300 }, "Capacity failed to reject new state.");
            Check(service.Create(Key, Order, default).Outcome == SampleOrderOutcome.Replayed, "Capacity rejected an existing key.");
            clock.ShiftUtc(TimeSpan.FromDays(365));
            Check(service.Get(initial.Id, default) == initial, "Wall-clock changes expired retained state.");
            clock.Advance(TimeSpan.FromSeconds(299));
            Check(service.Create(Key, Order, default).Order == initial && service.Create("overflow-key-0001", Order, default).RetryAfterSeconds == 1, "Replay changed expiry or capacity timing.");
            clock.Advance(TimeSpan.FromSeconds(1));
            Check(service.Get(initial.Id, default) is null, "Expiry retained an order.");
            var reused = service.Create(Key, Order, default);
            Check(reused.Outcome == SampleOrderOutcome.Created && reused.Order!.Id != initial.Id, "Expired key could not create a new order.");
            foreach (var key in new[] { "", "short", new string('a', 65), "bad key 000000000", "non-ascii-00000é" })
            { try { service.Create(key, Order, default); throw new Exception("Unbounded key accepted."); } catch (ArgumentException) { } }
            foreach (var invalid in new[] { Order with { Product = null }, Order with { Product = "private-data" }, Order with { Quantity = 0 }, Order with { Quantity = 101 } })
            { try { service.Create(Key, invalid, default); throw new Exception("Invalid synthetic payload accepted."); } catch (ArgumentException) { } }
            return Task.CompletedTask;
        });
        await run("Sample orders: HTTP validates bounded writes, replay headers, locations, conflicts, expiry, and OpenAPI", async () =>
        {
            var clock = new ReviewReleaseChecks.ManualTime(); await using var fixture = await Fixture.Start(clock);
            using var client = new HttpClient { BaseAddress = new(fixture.App.Urls.Single()) };
            foreach (var key in new string?[] { null, "short", new string('a', 65), "bad key 000000000" })
            { using var invalid = await Send(client, key); Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid key passed HTTP validation."); }
            using (var multiple = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(Order) })
            { multiple.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { Key, "second-key-000001" }); using var response = await client.SendAsync(multiple); Check(response.StatusCode == HttpStatusCode.BadRequest, "Multiple keys were accepted."); }
            foreach (var body in new[] { "{}", "null", "{", "{\"product\":\"sample-widget\",\"quantity\":0}", "{\"product\":\"private-data\",\"quantity\":1}", "{\"product\":\"sample-widget\",\"quantity\":1,\"customer\":\"private\"}" })
            { using var invalid = await Send(client, Key, body); Check(invalid.StatusCode == HttpStatusCode.BadRequest && invalid.Content.Headers.ContentType?.MediaType == "application/problem+json", "Invalid payload passed HTTP validation."); }
            using (var large = await Send(client, Key, new string(' ', 5000) + "{}")) Check(large.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized body status: " + large.StatusCode + "; " + await large.Content.ReadAsStringAsync());
            using var created = await Send(client, Key); var order = await created.Content.ReadFromJsonAsync<SampleOrderResponse>();
            Check(created.StatusCode == HttpStatusCode.Created && created.Headers.GetValues("Idempotency-Replayed").Single() == "false" && created.Headers.CacheControl?.NoStore == true, "Creation metadata is invalid.");
            Check(created.Headers.Location?.OriginalString == Path + "/" + order!.Id && order.Instance == "orders-backend" && order.ExpiresAt - order.CreatedAt == SampleOrderService.Retention, "Order location, identity, or retention is invalid.");
            using var read = await client.GetAsync(created.Headers.Location);
            Check(read.StatusCode == HttpStatusCode.OK && await read.Content.ReadFromJsonAsync<SampleOrderResponse>() == order && read.Headers.CacheControl?.NoStore == true, "Created location cannot read the order.");
            using var replay = await Send(client, Key, "{\"quantity\":2,\"product\":\"sample-widget\"}");
            Check(replay.StatusCode == HttpStatusCode.Created && await replay.Content.ReadFromJsonAsync<SampleOrderResponse>() == order && replay.Headers.GetValues("Idempotency-Replayed").Single() == "true", "Semantic duplicate did not replay its original result.");
            using (var conflict = await Send(client, Key, "{\"product\":\"sample-widget\",\"quantity\":3}")) Check(conflict.StatusCode == HttpStatusCode.Conflict, "Conflicting body was accepted.");
            for (var i = 1; i < SampleOrderService.Capacity; i++) fixture.Service.Create($"capacity-{i:D12}", Order, default);
            using (var capacity = await Send(client, "overflow-key-0001")) Check(capacity.StatusCode == HttpStatusCode.ServiceUnavailable && capacity.Headers.RetryAfter?.Delta == SampleOrderService.Retention, "Capacity response omitted retry guidance.");
            clock.Advance(SampleOrderService.Retention);
            using (var expired = await client.GetAsync(created.Headers.Location)) Check(expired.StatusCode == HttpStatusCode.NotFound, "Expired order remained available.");
            using (var reused = await Send(client, Key)) Check(reused.StatusCode == HttpStatusCode.Created && (await reused.Content.ReadFromJsonAsync<SampleOrderResponse>())!.Id != order!.Id, "Expired HTTP key did not reset.");
            var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
            var paths = document.GetProperty("paths");
            Check(paths.GetProperty(Path).TryGetProperty("post", out var operation) && paths.GetProperty(Path + "/{id}").TryGetProperty("get", out _) &&
                operation.GetProperty("parameters").EnumerateArray().Any(item => item.GetProperty("name").GetString() == "Idempotency-Key"), "OpenAPI omitted orders or their key header.");
        });
        await run("Sample orders: a pinned gateway retry replays a committed order after a lost response", async () =>
        {
            await using var fixture = await Fixture.Start(new ReviewReleaseChecks.ManualTime(), loseFirstResponse: true);
            await using var gateway = await RedirectChecks.Fixture.Start();
            var config = gateway.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await config.TryAddClusterConfig(new() { ClusterId = "orders", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = fixture.App.Urls.Single() } } })).Success, "Order cluster failed.");
            Check((await config.TryAddRouteConfig(new() { RouteId = "orders", ClusterId = "orders", Match = new() { Path = Path + "/{**rest}" } })).Success, "Order route failed.");
            await gateway.Bind("orders", "request-retry", "{\"enabled\":true,\"maxRetries\":1,\"retryNonIdempotent\":true,\"backoffBaseMs\":0,\"backoffJitterMs\":0}");
            await gateway.WaitRoute("orders");
            using var response = await Send(gateway.Client, Key);
            var order = await response.Content.ReadFromJsonAsync<SampleOrderResponse>();
            Check(response.StatusCode == HttpStatusCode.Created && order!.Id == fixture.CommittedId && fixture.Posts == 2 && response.Headers.GetValues("Idempotency-Replayed").Single() == "true", "Retry result: status=" + response.StatusCode + ", id=" + order?.Id + ", committed=" + fixture.CommittedId + ", posts=" + fixture.Posts + ", headers=" + response.Headers);
            using var read = await gateway.Client.GetAsync(response.Headers.Location);
            Check(read.StatusCode == HttpStatusCode.OK && await read.Content.ReadFromJsonAsync<SampleOrderResponse>() == order, "Relative order location failed through the gateway.");
        });
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string? key, string? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body ?? JsonSerializer.Serialize(Order), Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture(WebApplication app, ISampleOrderService service) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public ISampleOrderService Service { get; } = service;
        public int Posts;
        public Guid? CommittedId;
        public static async Task<Fixture> Start(TimeProvider clock, bool loseFirstResponse = false)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddSingleton(clock); builder.Services.AddSingleton(new SampleInstanceIdentity("orders-backend"));
            builder.Services.AddControllers().AddApplicationPart(typeof(LocalServiceController).Assembly); builder.Services.AddSampleOrders();
            var app = builder.Build(); var fixture = new Fixture(app, app.Services.GetRequiredService<ISampleOrderService>());
            app.UseExceptionHandler(); app.UseStatusCodePages();
            app.Use(async (context, next) =>
            {
                if (context.Request.Method == "POST" && context.Request.Path == Path && Interlocked.Increment(ref fixture.Posts) == 1 && loseFirstResponse)
                {
                    var request = await JsonSerializer.DeserializeAsync<CreateSampleOrderRequest>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
                    fixture.CommittedId = fixture.Service.Create(context.Request.Headers["Idempotency-Key"]!, request!, context.RequestAborted).Order!.Id;
                    context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>()!.Enabled = false;
                    context.Response.StatusCode = 503; context.Response.ContentLength = 0; return;
                }
                await next(context);
            });
            app.MapControllers(); app.MapOpenApi();
            try { await app.StartAsync(); return fixture; } catch { await app.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
}
