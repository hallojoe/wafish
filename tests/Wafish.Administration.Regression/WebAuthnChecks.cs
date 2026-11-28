using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure;

internal static partial class WebAuthnChecks
{
    private const string Origin = "https://localhost";
    internal static async Task RunAsync(Func<string, Func<Task>, Task> run,
        Func<string, Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await BoundsChecks(run);
        await SecurityChecks(run, start, password);
        await LifecycleChecks(run, start, password);
        await run("WebAuthn: signed enrollment, login, revocation, and password recovery preserve session contracts", async () =>
        {
            foreach (var mode in new[] { "DefaultJwt", "CustomJwt" })
            {
                await using var fixture = await Fixture.Start(start, password, mode);
                using var authenticator = new Authenticator();
                var token = await fixture.PasswordLogin();
                fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                var begin = await fixture.Post("enrollment/options", new { name = "Security key", password });
                var credential = authenticator.Create(begin.GetProperty("options"), fixture.Origin);
                var enrolled = await fixture.Post("enrollment/complete", new { challengeId = begin.GetProperty("challengeId").GetString(), credential }, HttpStatusCode.Created);
                var id = enrolled.GetProperty("id").GetString()!;
                using (var rejected = await fixture.Client.GetAsync("/ops/api/auth/status")) Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "Enrollment retained the old JWT session.");
                fixture.Client.DefaultRequestHeaders.Authorization = null;
                var assertion = await fixture.BeginLogin();
                var signed = authenticator.Get(assertion.GetProperty("options"), fixture.Origin);
                var loggedIn = await fixture.Post("login/complete", new { challengeId = assertion.GetProperty("challengeId").GetString(), credential = signed });
                fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", loggedIn.GetProperty("token").GetString());
                using (var authorized = await fixture.Client.GetAsync("/ops/api/auth/status")) Check(authorized.IsSuccessStatusCode, "Verified passkey did not create an active session.");
                using (var replay = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new { challengeId = assertion.GetProperty("challengeId").GetString(), credential = signed }))
                    Check(replay.StatusCode == HttpStatusCode.Unauthorized, "Assertion challenge was reusable.");
                using (var removed = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/credentials/remove", new { credentialId = id, password }))
                    Check(removed.StatusCode == HttpStatusCode.NoContent, "Authenticator removal failed: " + await removed.Content.ReadAsStringAsync());
                using (var revoked = await fixture.Client.GetAsync("/ops/api/auth/status")) Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "Removal retained JWT sessions.");
                fixture.Client.DefaultRequestHeaders.Authorization = null;
                using (var unavailable = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/options", new { username = "admin" }))
                    Check(unavailable.StatusCode == HttpStatusCode.Unauthorized, "Removed authenticator remained available.");
                fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", await fixture.PasswordLogin());
                using var recovery = await fixture.Client.GetAsync("/ops/api/auth/status");
                Check(recovery.IsSuccessStatusCode, "Password recovery stopped working after authenticator removal.");
            }
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required WebApplication App { get; init; }
        internal required HttpClient Client { get; init; }
        internal required string DirectoryPath { get; init; }
        internal required string Origin { get; init; }
        internal required Clock Clock { get; init; }
        internal required string Password { get; init; }
        internal static async Task<Fixture> Start(Func<string, Action<IServiceCollection>, Task<WebApplication>> start, string password, string mode, Action<DashboardOptions>? configure = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-webauthn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                var origin = "http://localhost:" + port;
                var clock = new Clock();
                var app = await start(mode, services =>
                {
                    services.AddSingleton<TimeProvider>(clock);
                    services.PostConfigure<Wafish.Storage.StorageOptions>(options => options.Sqlite.ConnectionString =
                        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "gateway.db"), Password = password, Pooling = false }.ToString());
                    services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options => options.Listen(IPAddress.Loopback, port));
                    services.PostConfigure<DashboardOptions>(options =>
                {
                    options.Auth.TwoFactorStatePath = Path.Combine(directory, "totp.json");
                    options.Auth.WebAuthn = new() { Enabled = true, RelyingPartyId = "localhost", Origins = [origin], CredentialStorePath = Path.Combine(directory, "credentials.json") };
                    configure?.Invoke(options);
                });
                });
                var client = new HttpClient(new HttpClientHandler { UseCookies = false })
                { BaseAddress = new Uri(origin) };
                client.DefaultRequestHeaders.Add("Origin", origin);
                return new() { App = app, Client = client, DirectoryPath = directory, Origin = origin, Clock = clock, Password = password };
            }
            catch { Directory.Delete(directory, true); throw; }
        }
        internal async Task<string> Enroll(Authenticator authenticator, string? code = null)
        {
            Client.DefaultRequestHeaders.Authorization = new("Bearer", await PasswordLogin(code));
            var begin = await Post("enrollment/options", new { name = "Security key", password = Password, twoFactorCode = code });
            var enrolled = await Post("enrollment/complete", new { challengeId = begin.GetProperty("challengeId").GetString(),
                credential = authenticator.Create(begin.GetProperty("options"), Origin) }, HttpStatusCode.Created);
            Client.DefaultRequestHeaders.Authorization = null;
            return enrolled.GetProperty("id").GetString()!;
        }
        internal async Task<string> PasswordLogin(string? code = null)
        {
            using var response = await Client.PostAsJsonAsync("/ops/login", new { username = "admin", password = Password, twoFactorCode = code });
            Check(response.IsSuccessStatusCode, "Password login failed: " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Check(body.TryGetProperty("token", out var token), "Password login returned no session: " + body);
            return token.GetString()!;
        }
        internal async Task<JsonElement> Post(string path, object value, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await Client.PostAsJsonAsync("/ops/api/webauthn/" + path, value);
            Check(response.StatusCode == expected, path + ": " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        internal async Task<JsonElement> BeginLogin()
        {
            using var response = await Client.PostAsJsonAsync("/ops/api/webauthn/login/options", new { username = "admin" });
            Check(response.IsSuccessStatusCode, "Sign-in challenge failed: " + await response.Content.ReadAsStringAsync());
            var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("wafish_webauthn="));
            Check(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
                && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "Challenge cookie lost its protections.");
            Client.DefaultRequestHeaders.Remove("Cookie"); Client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Directory.Delete(DirectoryPath, true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
