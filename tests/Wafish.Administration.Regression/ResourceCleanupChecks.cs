using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wafish.Storage;

internal static class ResourceCleanupChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run,
        Func<Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await run("Cleanup: failed assertions release clients, sockets, hosts, and temporary database files", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            HttpClient? client = null;
            ClientWebSocket? socket = null;
            Uri? address = null;
            void Configure(IServiceCollection services) => services.Configure<StorageOptions>(options => options.Sqlite.ConnectionString =
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "test.db"), Password = password }.ToString());
            try
            {
                try
                {
                    await using var host = await start(Configure);
                    address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                    using (client = new HttpClient { BaseAddress = address })
                    using (socket = new ClientWebSocket())
                    {
                        client.DefaultRequestHeaders.Add("X-Api-Key", password);
                        using var response = await client.GetAsync("/ops/api/info");
                        response.EnsureSuccessStatusCode();
                        socket.Options.SetRequestHeader("X-Api-Key", password);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/ops/hubs/overview" }.Uri, timeout.Token);
                        throw new ExpectedFailure();
                    }
                }
                catch (ExpectedFailure) { }
                if (client == null || socket == null || address == null) throw new Exception("Fixture did not reach the failed assertion.");
                try { await client.GetAsync("/ops/api/info"); throw new Exception("HTTP client remained usable."); }
                catch (ObjectDisposedException) { }
                if (socket.State == WebSocketState.Open) throw new Exception("WebSocket remained open.");
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(address.Host, address.Port); throw new Exception("Host listener remained open."); }
                catch (SocketException) { }
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
                if (Directory.Exists(directory)) throw new Exception("Failed case retained its files.");
                Directory.CreateDirectory(directory);
                await using var next = await start(Configure);
                await next.StopAsync();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });
        await run("Cleanup: failed host startup disposes services before the next case", async () =>
        {
            var tracker = new StartupFailure();
            try
            {
                await using var unexpected = await start(services => services.AddSingleton<IHostedService>(_ => tracker));
                throw new Exception("Expected startup failure.");
            }
            catch (ExpectedFailure) { }
            if (!tracker.Disposed) throw new Exception("Failed startup retained its owned service.");
            await using var next = await start(_ => { });
            await next.StopAsync();
        });
    }
    private sealed class ExpectedFailure : Exception;
    private sealed class StartupFailure : IHostedService, IDisposable
    {
        public bool Disposed;
        public Task StartAsync(CancellationToken cancellationToken) => throw new ExpectedFailure();
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}
