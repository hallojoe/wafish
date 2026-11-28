using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;

internal static partial class WebAuthnChecks
{
    private static async Task BoundsChecks(Func<string, Func<Task>, Task> run)
    {
        await run("WebAuthn: configuration rejects unsafe origins, unsupported modes, and shared state paths", () =>
        {
            DashboardOptions Valid() => new() { Auth = new() { AuthMode = DashboardAuthMode.DefaultJwt,
                WebAuthn = new() { Enabled = true, RelyingPartyId = "localhost", Origins = ["https://localhost"], CredentialStorePath = Path.Combine(Path.GetTempPath(), "webauthn-validation.json") } } };
            Check(DashboardWebAuthnConfiguration.Create(new()) is null, "Disabled WebAuthn initialized a store.");
            Check(DashboardWebAuthnConfiguration.Create(Valid())!.Account == "admin", "Default account changed.");
            var custom = Valid(); custom.Auth.AuthMode = DashboardAuthMode.CustomJwt; custom.Auth.JwtUsername = "operator";
            Check(DashboardWebAuthnConfiguration.Create(custom)!.Account == "operator", "Custom account changed.");
            foreach (var mutate in new Action<DashboardOptions>[] {
                o => o.Auth.AuthMode = DashboardAuthMode.None,
                o => o.Auth.WebAuthn.RelyingPartyId = "https://localhost",
                o => o.Auth.WebAuthn.RelyingPartyId = "localhost.",
                o => o.Auth.WebAuthn.RelyingPartyName = "",
                o => o.Auth.WebAuthn.Origins = [],
                o => o.Auth.WebAuthn.Origins = Enumerable.Repeat("https://localhost", 17).ToArray(),
                o => o.Auth.WebAuthn.Origins = ["https://localhost", "https://localhost/"],
                o => o.Auth.WebAuthn.Origins = ["https://other.test"],
                o => o.Auth.WebAuthn.Origins = ["https://localhost/path"],
                o => o.Auth.WebAuthn.Origins = ["https://user@localhost"],
                o => o.Auth.WebAuthn.Origins = ["https://localhost?query"],
                o => o.Auth.WebAuthn.Origins = ["https://localhost#fragment"],
                o => { o.Auth.WebAuthn.RelyingPartyId = "example.test"; o.Auth.WebAuthn.Origins = ["http://example.test"]; },
                o => o.Auth.WebAuthn.Origins = ["invalid"],
                o => o.Auth.WebAuthn.CredentialStorePath = "",
                o => o.Auth.WebAuthn.CredentialStorePath = o.Auth.TwoFactorStatePath,
                o => { o.Auth.AuthMode = DashboardAuthMode.CustomJwt; o.Auth.JwtUsername = ""; }
            }) { var options = Valid(); mutate(options); Throws<OptionsValidationException>(() => DashboardWebAuthnConfiguration.Create(options)); }
            return Task.CompletedTask;
        });
        await run("WebAuthn: challenge storage bounds capacity, lifetime, ownership, purpose, and replay", () =>
        {
            var clock = new Clock(); var challenges = new DashboardWebAuthnChallenges(clock);
            string Add() => challenges.Add("login", "admin", "browser", 1, 2, new object());
            var first = Add(); Check(challenges.Take(first, "login", "admin", "browser") is { SessionGeneration: 1, CredentialGeneration: 2 }, "Challenge lost binding.");
            Check(challenges.Take(first, "login", "admin", "browser") is null, "Challenge replay succeeded.");
            Check(challenges.Take(Add(), "enroll", "admin", "browser") is null, "Wrong operation accepted.");
            Check(challenges.Take(Add(), "login", "other", "browser") is null, "Wrong account accepted.");
            Check(challenges.Take(Add(), "login", "admin", "other") is null, "Wrong owner accepted.");
            Check(challenges.Take(null, "login", "admin", "browser") is null, "Missing identifier accepted.");
            var expired = Add(); clock.Advance(TimeSpan.FromMinutes(2));
            Check(challenges.Take(expired, "login", "admin", "browser") is null, "Boundary expiry accepted.");
            for (var i = 0; i < DashboardWebAuthnChallenges.Capacity; i++) Add();
            Throws<InvalidOperationException>(() => Add()); clock.Advance(TimeSpan.FromMinutes(2));
            Check(Add().Length == 43, "Expired capacity was not reclaimed.");
            return Task.CompletedTask;
        });
        await run("WebAuthn: private credential persistence survives restart and rejects corruption, stale updates, and capacity overflow", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-webauthn-store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "credentials.json");
            var config = new DashboardWebAuthnConfiguration("admin", "localhost", "Test", ["https://localhost"], path);
            DashboardWebAuthnStore.Credential Credential(int id) => new(WebEncoders.Base64UrlEncode(BitConverter.GetBytes(id)), "Key " + id, [1, 2, 3], 0, false, false, DateTimeOffset.UtcNow, null);
            try
            {
                byte[] handle;
                using (var store = new DashboardWebAuthnStore(config))
                {
                    handle = store.Read().UserHandle;
                    using var competitor = new DashboardWebAuthnStore(config); Throws<IOException>(() => competitor.Read());
                    if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Credential file permissions are not private.");
                    var key = Credential(1); store.Add(0, key);
                    Throws<InvalidOperationException>(() => store.Add(0, Credential(2)));
                    Throws<InvalidOperationException>(() => store.Add(1, key));
                    store.RecordUse(1, key, 1, false, DateTimeOffset.UtcNow);
                    Throws<InvalidOperationException>(() => store.RecordUse(1, key, 2, false, DateTimeOffset.UtcNow));
                    Throws<InvalidOperationException>(() => store.RecordUse(1, store.Read().Credentials[0], 1, false, DateTimeOffset.UtcNow));
                    for (var id = 2; id <= 20; id++) store.Add(store.Read().Generation, Credential(id));
                    Throws<InvalidOperationException>(() => store.Add(store.Read().Generation, Credential(21)));
                    Check(!store.Remove("unknown"), "Unknown removal reported success.");
                }
                using (var reopened = new DashboardWebAuthnStore(config))
                {
                    Check(reopened.Read().UserHandle.SequenceEqual(handle) && reopened.Read().Credentials.Length == 20, "Restart lost credentials or account handle.");
                    Check(reopened.Read().Credentials[0].SignCount == 1, "Counter was not persisted.");
                    Check(reopened.Remove(Credential(1).Id), "Removal failed.");
                }
                var valid = File.ReadAllText(path);
                foreach (var invalid in new[] { "{}", "null", valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"), valid.Replace("\"Account\":\"admin\"", "\"Account\":\"other\""), new string(' ', 1024 * 1024 + 1) })
                {
                    File.WriteAllText(path, invalid);
                    using var corrupt = new DashboardWebAuthnStore(config);
                    var failed = false;
                    try { corrupt.Read(); } catch (Exception error) when (error is JsonException or InvalidDataException) { failed = true; }
                    Check(failed && File.ReadAllText(path) == invalid, "Corrupt credentials were accepted or overwritten.");
                }
                File.WriteAllText(path, valid);
                var disposed = new DashboardWebAuthnStore(config); disposed.Dispose(); Throws<ObjectDisposedException>(() => disposed.Read());
            }
            finally { Directory.Delete(directory, true); }
            return Task.CompletedTask;
        });
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
