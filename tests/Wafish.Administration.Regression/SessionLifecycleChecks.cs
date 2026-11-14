using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;

internal static class SessionLifecycleChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run,
        Func<string, Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await run("Sessions: bounded capacity, distinct tokens, generation races, revocation, expiry, and disposal", () =>
        {
            var clock = new SessionTestClock();
            using var store = new DashboardSessionStore(clock);
            var generation = store.Generation;
            Require(store.TryCreate("admin", password, generation, out var one), "Session creation failed.");
            Require(store.TryCreate("admin", password, generation, out var two) && one != two, "Concurrent logins share a token.");
            var lifetime = store.Lifetime(one);
            store.Revoke(one);
            Require(!store.IsActive(one) && lifetime.IsCancellationRequested && store.IsActive(two), "Revocation affected the wrong session.");
            store.Revoke(one); store.Revoke(null);
            for (var i = 1; i < DashboardSessionStore.Capacity; i++) Require(store.TryCreate("admin", password, generation, out _), "Premature session capacity rejection.");
            Require(!store.TryCreate("admin", password, generation, out _), "Unbounded session registry.");
            clock.Advance(TimeSpan.FromHours(8));
            Require(!store.IsActive(two), "Expired session remained active.");
            Require(store.TryCreate("admin", password, generation, out var renewed), "Expired capacity was not reclaimed.");
            Require(DashboardJwtHelper.ValidateToken(renewed, password, clock).Valid, "Controlled-clock token is invalid.");
            store.InvalidateAll();
            Require(!store.IsActive(renewed) && !store.TryCreate("admin", password, generation, out _), "Security-change race issued a stale session.");
            Require(store.TryCreate("admin", password, store.Generation, out _), "New generation rejected login.");
            store.Dispose();
            Require(!store.TryCreate("admin", password, store.Generation, out _) && clock.TimerCount == 0, "Shutdown leaked sessions or timers.");
            return Task.CompletedTask;
        });

        foreach (var mode in new[] { "DefaultJwt", "CustomJwt" })
        {
            await run($"Sessions: {mode} sign-out, security changes, expiry, and concurrent API/hub requests", async () =>
            {
                var clock = new SessionTestClock();
                await using var host = await start(mode, services => services.AddSingleton<TimeProvider>(clock));
                var auth = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
                using var client = Client(host);
                var first = await Login(client, password);
                var peer = await Login(client, password);
                Require(first != peer, "Distinct logins share a bearer token.");
                using var firstHub = await Connect(client.BaseAddress!, "overview", first);
                using var peerHub = await Connect(client.BaseAddress!, "traffic", peer);
                client.DefaultRequestHeaders.Authorization = new("Bearer", first);
                using (var response = await client.PostAsync("/ops/logout", null)) Require(response.IsSuccessStatusCode, "Sign-out failed.");
                await Closed(firstHub);
                await Rejected(client, first);
                client.DefaultRequestHeaders.Authorization = new("Bearer", peer);
                using (var response = await client.GetAsync("/ops/api/info")) Require(response.IsSuccessStatusCode, "Sign-out revoked another session.");
                var secret = TotpHelper.GenerateSecret();
                try
                {
                    using (var response = await client.PostAsJsonAsync("/ops/api/2fa/verify", new { secret, code = TotpHelper.GenerateCode(secret) }))
                        Require(response.IsSuccessStatusCode, "Two-factor enable failed.");
                    await Closed(peerHub);
                    await Rejected(client, peer);
                    client.DefaultRequestHeaders.Authorization = null;
                    using (var challenge = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password }))
                    {
                        using var body = JsonDocument.Parse(await challenge.Content.ReadAsStringAsync());
                        Require(body.RootElement.GetProperty("requiresTwoFactor").GetBoolean(), "New login bypassed two-factor authentication.");
                    }
                    var secured = await Login(client, password, TotpHelper.GenerateCode(secret));
                    client.DefaultRequestHeaders.Authorization = new("Bearer", secured);
                    using (var response = await client.PostAsync("/ops/api/2fa/disable", null)) Require(response.IsSuccessStatusCode, "Two-factor disable failed.");
                    await Rejected(client, secured);
                    client.DefaultRequestHeaders.Authorization = null;
                    var expiring = await Login(client, password);
                    using var expiringHub = await Connect(client.BaseAddress!, "overview", expiring);
                    clock.Advance(TimeSpan.FromHours(8));
                    await Closed(expiringHub);
                    await Rejected(client, expiring);
                    var fresh = await Login(client, password);
                    client.DefaultRequestHeaders.Authorization = new("Bearer", fresh);
                    using var valid = await client.GetAsync("/ops/api/info");
                    Require(valid.IsSuccessStatusCode, "Expired session prevented a fresh login.");
                }
                finally { if (File.Exists(auth.TwoFactorStatePath)) File.Delete(auth.TwoFactorStatePath); }
                await host.StopAsync();
            });
        }

        foreach (var mode in new[] { "BasicAuth", "ApiKey" })
        {
            await run($"Sessions: {mode} credential rotation terminates realtime access and rejects old credentials", async () =>
            {
                var clock = new SessionTestClock();
                await using var host = await start(mode, services => services.AddSingleton<TimeProvider>(clock));
                var auth = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
                using var client = Client(host);
                var header = mode == "ApiKey" ? "X-Api-Key" : "Authorization";
                var old = mode == "ApiKey" ? password : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + password));
                client.DefaultRequestHeaders.Add(header, old);
                using var hub = await Connect(client.BaseAddress!, "overview", null, header, old);
                auth.ApiKey = auth.JwtPassword = password + "-rotated";
                clock.Advance(TimeSpan.FromSeconds(2));
                await Closed(hub);
                using (var response = await client.GetAsync("/ops/api/info")) Require(response.StatusCode == HttpStatusCode.Unauthorized, "Rotated credential still works.");
                using (var response = await client.PostAsync("/ops/hubs/overview/negotiate?negotiateVersion=1", null)) Require(response.StatusCode == HttpStatusCode.Unauthorized, "Old credential opened another hub.");
                client.DefaultRequestHeaders.Remove(header);
                client.DefaultRequestHeaders.Add(header, mode == "ApiKey" ? auth.ApiKey : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + auth.JwtPassword)));
                using var valid = await client.GetAsync("/ops/api/info");
                Require(valid.IsSuccessStatusCode, "Rotated credential was rejected.");
                await host.StopAsync();
            });
        }
    }
    private static async Task<string> Login(HttpClient client, string password, string? twoFactorCode = null)
    {
        using var response = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password, twoFactorCode });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }
    private static async Task Rejected(HttpClient client, string token)
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var request = new HttpRequestMessage(i % 2 == 0 ? HttpMethod.Get : HttpMethod.Post,
                i % 2 == 0 ? "/ops/api/info" : "/ops/hubs/overview/negotiate?negotiateVersion=1");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.Unauthorized, "Revoked or expired session retained API/hub access.");
        }));
    }
    private static HttpClient Client(WebApplication host) => new(new HttpClientHandler { UseCookies = false })
    { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(10) };
    private static async Task<ClientWebSocket> Connect(Uri address, string hub, string? token, string? header = null, string? value = null)
    {
        var socket = new ClientWebSocket();
        if (token != null) socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        if (header != null) socket.Options.SetRequestHeader(header, value!);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/ops/hubs/" + hub }.Uri, timeout.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Require(Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("{}"), "Hub handshake failed.");
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    private static async Task Closed(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;
            }
        }
        catch (WebSocketException) { return; }
        Require(socket.State != WebSocketState.Open, "Revoked connection stayed open.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
