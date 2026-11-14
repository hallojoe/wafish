using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wafish.Dashboard.Infrastructure.Notifications;
using Wafish.Storage;

internal static class ShutdownChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run,
        Func<Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await run("Shutdown: connected hubs close while queued notifications retain exact accounting", async () =>
        {
            using var handler = new BlockedDelivery();
            await using var host = await start(services =>
            {
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.AddSingleton<IWebhookSettingsRepository>(new Settings());
                services.AddSingleton<IHttpClientFactory>(new Factory(handler));
            });
            var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var overview = await Connect(address, "overview", password);
            using var traffic = await Connect(address, "traffic", password);
            var dispatcher = host.Services.GetRequiredService<NotificationDispatcher>();
            dispatcher.EnqueueConfigChange("shutdown-test", "committed-revision", "fixture");
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 100; index++) dispatcher.EnqueueConfigChange("shutdown-test", "committed-revision", "fixture");
            var overviewClosed = ReadUntilClosed(overview, password);
            var trafficClosed = ReadUntilClosed(traffic, password);
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(overviewClosed, trafficClosed).WaitAsync(TimeSpan.FromSeconds(5));
            var result = dispatcher.GetSnapshot();
            Require(result.Submitted == 101 && result.Discarded == 101 && result.Queued == 0 && result.InFlight == 0,
                "Shutdown lost notification ownership.");
            Require(result.Delivered == 0 && result.Failed == 0, "Cancelled delivery reported success or failure.");
            Require(!handler.Body.Contains(password, StringComparison.Ordinal), "Notification exposed the administration credential.");
        });
    }

    private static async Task<ClientWebSocket> Connect(Uri address, string hub, string password)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("X-Api-Key", password);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/ops/hubs/" + hub }.Uri, timeout.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
            var handshake = await socket.ReceiveAsync(new byte[4096], timeout.Token);
            Require(handshake.MessageType == WebSocketMessageType.Text, "Hub handshake failed.");
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    private static async Task ReadUntilClosed(ClientWebSocket socket, string secret)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                var response = await socket.ReceiveAsync(buffer, timeout.Token);
                if (response.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", timeout.Token);
                    return;
                }
                Require(!Encoding.UTF8.GetString(buffer, 0, response.Count).Contains(secret, StringComparison.Ordinal), "Hub exposed credentials.");
            }
        }
        catch (WebSocketException) { } // Server shutdown may abort the transport after its grace period.
    }
    private sealed class Settings : IWebhookSettingsRepository
    {
        public Task<WebhookSettingsData> LoadAsync(CancellationToken ct = default) => Task.FromResult(new WebhookSettingsData
        {
            Platforms = new() { ["generic"] = [new() { Url = "https://example.test/shutdown" }] },
            EnabledEvents = ["shutdown-test"], RetryCount = 0, CooldownSeconds = 0
        });
        public Task SaveAsync(WebhookSettingsData settings, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class BlockedDelivery : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
