using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;

internal static partial class WebAuthnChecks
{
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _start.AddTicks(_ticks);
        internal void Advance(TimeSpan? time = null) => _ticks += (time ?? TimeSpan.FromMinutes(1)).Ticks;
    }
    private static async Task SecurityChecks(Func<string, Func<Task>, Task> run,
        Func<string, Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await run("WebAuthn: signed assertions reject wrong origin, RP, verification, signature, user handle, and counter rollback", async () =>
        {
            await using var fixture = await Fixture.Start(start, password, "DefaultJwt");
            using var key = new Authenticator(); await fixture.Enroll(key);
            foreach (var failure in new[] { "origin", "rp", "verification", "signature", "user", "cross-origin" })
            {
                fixture.Clock.Advance();
                var challenge = await fixture.BeginLogin();
                var assertion = key.Get(challenge.GetProperty("options"), failure == "origin" ? "https://attacker.test" : fixture.Origin,
                    rp: failure == "rp" ? "other.localhost" : "localhost", verified: failure != "verification",
                    crossOrigin: failure == "cross-origin", userHandle: failure == "user" ? WebEncoders.Base64UrlEncode(new byte[32]) : null,
                    corruptSignature: failure == "signature");
                using var rejected = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new
                { challengeId = challenge.GetProperty("challengeId").GetString(), credential = assertion });
                Check(rejected.StatusCode == HttpStatusCode.Unauthorized, failure + " accepted: " + await rejected.Content.ReadAsStringAsync());
                Check(!rejected.Headers.TryGetValues("Set-Cookie", out var cookies) || !cookies.Any(cookie => cookie.StartsWith("dashboard_token=")), "Rejected assertion created a session.");
            }
            fixture.Clock.Advance();
            var good = await fixture.BeginLogin();
            await fixture.Post("login/complete", new { challengeId = good.GetProperty("challengeId").GetString(), credential = key.Get(good.GetProperty("options"), fixture.Origin) });
            fixture.Clock.Advance(); key.Counter--;
            var rollback = await fixture.BeginLogin();
            using var rejectedCounter = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new
            { challengeId = rollback.GetProperty("challengeId").GetString(), credential = key.Get(rollback.GetProperty("options"), fixture.Origin) });
            Check(rejectedCounter.StatusCode == HttpStatusCode.Unauthorized, "Signature counter rollback was accepted.");
        });
        await run("WebAuthn: expired and browser-mismatched challenges fail without issuing sessions", async () =>
        {
            await using var fixture = await Fixture.Start(start, password, "DefaultJwt");
            using var key = new Authenticator(); await fixture.Enroll(key);
            var expired = await fixture.BeginLogin(); fixture.Clock.Advance(TimeSpan.FromMinutes(3));
            using (var response = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new
            { challengeId = expired.GetProperty("challengeId").GetString(), credential = key.Get(expired.GetProperty("options"), fixture.Origin) }))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Expired challenge was accepted.");
            var mismatch = await fixture.BeginLogin(); fixture.Client.DefaultRequestHeaders.Remove("Cookie");
            fixture.Client.DefaultRequestHeaders.Add("Cookie", "wafish_webauthn=" + WebEncoders.Base64UrlEncode(new byte[32]));
            using (var response = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new
            { challengeId = mismatch.GetProperty("challengeId").GetString(), credential = key.Get(mismatch.GetProperty("options"), fixture.Origin) }))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "Challenge moved to another browser.");
            fixture.Client.DefaultRequestHeaders.Remove("Origin");
            using var noOrigin = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/options", new { username = "admin" });
            Check(noOrigin.StatusCode == HttpStatusCode.Forbidden, "Missing Origin bypassed ceremony protection.");
        });
        await run("WebAuthn: credential management requires permissions and fresh proof while login preserves TOTP", async () =>
        {
            var secret = TotpHelper.GenerateSecret();
            await using var fixture = await Fixture.Start(start, password, "DefaultJwt", options =>
            { options.Auth.EnableTwoFactor = true; options.Auth.TwoFactorSecret = secret; });
            var code = TotpHelper.GenerateCode(secret);
            fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", await fixture.PasswordLogin(code));
            using (var denied = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/enrollment/options", new { name = "key", password = "incorrect", twoFactorCode = code }))
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Enrollment accepted stale or incorrect password proof.");
            fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", await fixture.PasswordLogin(code));
            var options = fixture.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardOptions>>().Value;
            options.Auth.Permissions = [DashboardPermission.Read];
            using (var denied = await fixture.Client.GetAsync("/ops/api/webauthn/credentials")) Check(denied.StatusCode == HttpStatusCode.Forbidden, "Credential listing bypassed permissions.");
            options.Auth.Permissions = null;
            using var key = new Authenticator(); await fixture.Enroll(key, code);
            var missing = await fixture.BeginLogin();
            Check(missing.GetProperty("requiresTwoFactor").GetBoolean(), "Sign-in omitted its TOTP requirement.");
            using (var denied = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/login/complete", new
            { challengeId = missing.GetProperty("challengeId").GetString(), credential = key.Get(missing.GetProperty("options"), fixture.Origin) }))
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "WebAuthn bypassed configured TOTP.");
            var valid = await fixture.BeginLogin();
            var session = await fixture.Post("login/complete", new { challengeId = valid.GetProperty("challengeId").GetString(),
                credential = key.Get(valid.GetProperty("options"), fixture.Origin), twoFactorCode = TotpHelper.GenerateCode(secret) });
            Check(session.GetProperty("token").GetString() is { Length: > 0 }, "WebAuthn plus TOTP did not create a session.");
        });
        await run("WebAuthn: failed credential persistence preserves the active session and original credential state", async () =>
        {
            await using var fixture = await Fixture.Start(start, password, "DefaultJwt");
            using var key = new Authenticator();
            fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", await fixture.PasswordLogin());
            var begin = await fixture.Post("enrollment/options", new { name = "key", password });
            var path = Path.Combine(fixture.DirectoryPath, "credentials.json");
            var backup = path + ".saved"; File.Move(path, backup); Directory.CreateDirectory(path);
            try
            {
                using var failed = await fixture.Client.PostAsJsonAsync("/ops/api/webauthn/enrollment/complete", new
                { challengeId = begin.GetProperty("challengeId").GetString(), credential = key.Create(begin.GetProperty("options"), fixture.Origin) });
                Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable, "Failed persistence reported enrollment success.");
                using var session = await fixture.Client.GetAsync("/ops/api/auth/status");
                Check(session.IsSuccessStatusCode, "Failed persistence revoked the active session.");
                using var credentials = await fixture.Client.GetAsync("/ops/api/webauthn/credentials");
                Check((await credentials.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credentials").GetArrayLength() == 0, "Failed persistence changed in-memory credentials.");
                Check(!Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp").Any(), "Failed persistence retained a temporary credential file.");
            }
            finally { Directory.Delete(path); File.Move(backup, path); }
        });
    }
}
