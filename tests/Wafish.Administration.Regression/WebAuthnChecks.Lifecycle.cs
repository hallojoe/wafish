using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure.Auth;

internal static partial class WebAuthnChecks
{
    private static async Task LifecycleChecks(Func<string, Func<Task>, Task> run,
        Func<string, Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await run("WebAuthn: malformed enrollment, oversized requests, disabled ceremonies, and throttling fail closed", async () =>
        {
            await using (var disabled = await Fixture.Start(start, password, "DefaultJwt", options => options.Auth.WebAuthn.Enabled = false))
            {
                disabled.Client.DefaultRequestHeaders.Authorization = new("Bearer", await disabled.PasswordLogin());
                using var listing = await disabled.Client.GetAsync("/ops/api/webauthn/credentials");
                Check(!(await listing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean(), "Disabled feature reported enabled.");
                using var begin = await disabled.Client.PostAsJsonAsync("/ops/api/webauthn/login/options", new { username = "admin" });
                Check(begin.StatusCode == HttpStatusCode.NotFound, "Disabled ceremony was exposed.");
                Check(!File.Exists(Path.Combine(disabled.DirectoryPath, "credentials.json")), "Disabled feature created storage.");
            }
            await using var fixture = await Fixture.Start(start, password, "DefaultJwt");
            fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", await fixture.PasswordLogin());
            foreach (var raw in new object[] { new { }, new { id = "AQ", rawId = "AQ", type = "public-key", response = new { } },
                new { id = "AQ", rawId = "AQ", type = "public-key", response = new { attestationObject = "AA", clientDataJSON = "AA" } }, "invalid", "malformed-cbor" })
            {
                var begin = await fixture.Post("enrollment/options", new { name = "Key", password });
                var credential = raw is "malformed-cbor" ? new { id = "AQ", rawId = "AQ", type = "public-key", response = new
                { attestationObject = "AA", clientDataJSON = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
                { type = "webauthn.create", challenge = begin.GetProperty("options").GetProperty("challenge").GetString(), origin = fixture.Origin, crossOrigin = false })) } } : raw;
                using var rejected = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/enrollment/complete", new
                { challengeId = begin.GetProperty("challengeId").GetString(), credential });
                Check(rejected.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, "Malformed enrollment caused unexpected status: " + rejected.StatusCode);
            }
            using (var key = new Authenticator())
            {
                var challenge = await fixture.Post("enrollment/options", new { name = "Key", password });
                using var wrongOrigin = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/enrollment/complete", new
                { challengeId = challenge.GetProperty("challengeId").GetString(), credential = key.Create(challenge.GetProperty("options"), "https://attacker.test") });
                Check(wrongOrigin.StatusCode == HttpStatusCode.Unauthorized, "Enrollment accepted the wrong origin.");
            }
            using (var oversized = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/enrollment/options", new { name = new string('x', 70000), password }))
                Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Request body bound was not enforced.");
            fixture.Client.DefaultRequestHeaders.Authorization = null;
            HttpStatusCode status = 0;
            for (var i = 0; i < 30 && status != HttpStatusCode.TooManyRequests; i++)
            {
                using var denied = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/options", new { username = "unknown" }); status = denied.StatusCode;
                Check(status is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests, "Unknown account leaked unexpected response.");
            }
            Check(status == HttpStatusCode.TooManyRequests, "Ceremonies bypassed authentication throttling.");
        });
        await run("WebAuthn: counter and removal write failures cannot issue sessions or revoke existing access", async () =>
        {
            await using var fixture = await Fixture.Start(start, password, "DefaultJwt");
            using var key = new Authenticator(); var id = await fixture.Enroll(key);
            fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", await fixture.PasswordLogin());
            using (var description = await fixture.Client.GetAsync("/ops/api/webauthn/credentials/" + id)) Check(description.IsSuccessStatusCode, "Credential description was inaccessible.");
            using (var missing = await fixture.Client.GetAsync("/ops/api/webauthn/credentials/missing")) Check(missing.StatusCode == HttpStatusCode.NotFound, "Missing credential did not return 404.");
            using (var missing = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/credentials/remove", new { credentialId = "missing", password })) Check(missing.StatusCode == HttpStatusCode.NotFound, "Unknown removal reported success.");
            var challenge = await fixture.BeginLogin();
            var path = Path.Combine(fixture.DirectoryPath, "credentials.json"); var backup = path + ".saved";
            File.Move(path, backup); Directory.CreateDirectory(path);
            try
            {
                using var failed = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new
                { challengeId = challenge.GetProperty("challengeId").GetString(), credential = key.Get(challenge.GetProperty("options"), fixture.Origin) });
                Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable, "Counter write failure created a session.");
                Check(!failed.Headers.TryGetValues("Set-Cookie", out var cookies) || !cookies.Any(cookie => cookie.StartsWith("dashboard_token=")), "Counter write failure issued a cookie.");
                using var removal = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/credentials/remove", new { credentialId = id, password });
                Check(removal.StatusCode == HttpStatusCode.ServiceUnavailable, "Failed removal reported success.");
                using var active = await fixture.Client.GetAsync("/ops/api/auth/status"); Check(active.IsSuccessStatusCode, "Failed removal revoked active sessions.");
            }
            finally { Directory.Delete(path); File.Move(backup, path); }
            var pending = await fixture.BeginLogin();
            using (var removal = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/credentials/remove", new { credentialId = id, password })) Check(removal.IsSuccessStatusCode, "Removal failed.");
            fixture.Client.DefaultRequestHeaders.Authorization = null;
            using var stale = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new
            { challengeId = pending.GetProperty("challengeId").GetString(), credential = key.Get(pending.GetProperty("options"), fixture.Origin) });
            Check(stale.StatusCode == HttpStatusCode.Unauthorized, "Pending challenge bypassed credential revocation.");
        });
    }
}
