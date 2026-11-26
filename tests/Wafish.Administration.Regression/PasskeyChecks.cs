using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;

internal static class PasskeyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<string, Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await run("Passkeys: enrollment, signatures, replay, expiry, revocation, persistence, and password recovery", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-passkeys-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var clock = new Clock();
                var recoverySecret = TotpHelper.GenerateSecret();
                void Configure(IServiceCollection services)
                {
                    services.AddSingleton<TimeProvider>(clock);
                    services.Configure<DashboardOptions>(options =>
                    {
                        options.Auth.Passkeys = new() { Enabled = true, RelyingPartyId = "localhost", Origin = "https://localhost", StatePath = Path.Combine(directory, "keys.json") };
                        options.Auth.EnableTwoFactor = true; options.Auth.TwoFactorSecret = recoverySecret;
                        options.Auth.TwoFactorStatePath = Path.Combine(directory, "twofactor.json");
                    });
                }
                await using var host = await start("DefaultJwt", Configure);
                using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
                async Task<string> PasswordLogin()
                {
                    using var response = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password, twoFactorCode = TotpHelper.GenerateCode(recoverySecret) });
                    var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                    return json.GetProperty("token").GetString()!;
                }
                async Task<JsonElement> Options(string kind, object? body = null)
                {
                    using var response = await client.PostAsJsonAsync("/ops/api/passkeys/" + kind + "/options", body ?? new { });
                    Require(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                    return await response.Content.ReadFromJsonAsync<JsonElement>();
                }
                async Task<HttpStatusCode> Complete(string path, object body)
                {
                    using var response = await client.PostAsJsonAsync("/ops/api/passkeys/" + path, body);
                    return response.StatusCode;
                }
                Require((await client.GetAsync("/ops/api/passkeys")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous credential listing succeeded.");
                using var recoveryChallenge = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password });
                Require((await recoveryChallenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requiresTwoFactor").GetBoolean(), "Password recovery bypassed TOTP.");
                var token = await PasswordLogin(); client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                using var authenticator = new Authenticator();
                var enrollment = await Options("registration", new { name = "Test authenticator" });
                var registration = authenticator.Register(enrollment);
                using var created = await client.PostAsJsonAsync("/ops/api/passkeys/registration", registration);
                Require(created.StatusCode == HttpStatusCode.Created && created.Headers.Location is not null, "Registration lacks a created-resource location.");
                using var descriptor = await client.GetAsync(created.Headers.Location);
                Require(descriptor.IsSuccessStatusCode && !(await descriptor.Content.ReadAsStringAsync()).Contains("PublicKey", StringComparison.OrdinalIgnoreCase), "Credential location exposed key material or did not resolve.");
                Require((await client.GetAsync("/ops/api/passkeys/credential?id=missing")).StatusCode == HttpStatusCode.NotFound, "Unknown credential lookup succeeded.");
                Require(await Complete("registration", registration) == HttpStatusCode.BadRequest, "Enrollment replay succeeded.");
                Require(File.Exists(Path.Combine(directory, "keys.json")), "Credentials were not persisted.");
                if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(Path.Combine(directory, "keys.json")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Credential file permissions are too broad.");
                using var restoredSessions = new DashboardSessionStore(clock);
                using var restored = new DashboardPasskeyService(host.Services.GetRequiredService<IOptions<DashboardOptions>>(), restoredSessions, clock);
                Require(JsonSerializer.Serialize(await restored.ListAsync(default)).Contains("Test authenticator"), "Restart lost credentials.");
                client.DefaultRequestHeaders.Authorization = null;
                var assertion = authenticator.Assert(await Options("login"), 1);
                using var login = await client.PostAsJsonAsync("/ops/api/passkeys/login", assertion);
                Require(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
                var passkeyToken = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
                Require(login.Headers.GetValues("Set-Cookie").Single().Contains("secure", StringComparison.OrdinalIgnoreCase), "Passkey session lacks a secure cookie.");
                Require(await Complete("login", assertion) == HttpStatusCode.BadRequest, "Assertion replay succeeded.");
                var wrongOrigin = authenticator.Assert(await Options("login"), 2, "https://attacker.invalid");
                Require(await Complete("login", wrongOrigin) == HttpStatusCode.BadRequest, "Untrusted origin succeeded.");
                var invalidSignature = authenticator.Assert(await Options("login"), 2, corrupt: true);
                Require(await Complete("login", invalidSignature) == HttpStatusCode.BadRequest, "Invalid signature succeeded.");
                var expired = authenticator.Assert(await Options("login"), 2);
                clock.Advance(TimeSpan.FromMinutes(3));
                Require(await Complete("login", expired) == HttpStatusCode.BadRequest, "Expired challenge succeeded.");
                var staleCounter = authenticator.Assert(await Options("login"), 1);
                Require(await Complete("login", staleCounter) == HttpStatusCode.BadRequest, "Repeated signature counter succeeded.");
                var pending = authenticator.Assert(await Options("login"), 2);
                client.DefaultRequestHeaders.Authorization = new("Bearer", passkeyToken);
                using var list = await client.GetAsync("/ops/api/passkeys");
                var id = (await list.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();
                Require(await Complete("revoke", new { id }) == HttpStatusCode.OK, "Revocation failed.");
                Require((await client.GetAsync("/ops/api/info")).StatusCode == HttpStatusCode.Unauthorized, "Revocation retained an active session.");
                client.DefaultRequestHeaders.Authorization = null;
                Require(await Complete("login", pending) == HttpStatusCode.BadRequest, "Revoked credential completed a pending login.");
                client.DefaultRequestHeaders.Authorization = new("Bearer", await PasswordLogin());
                Require((await client.GetAsync("/ops/api/info")).IsSuccessStatusCode, "Password recovery failed after revoking the last passkey.");
                Require(await Complete("revoke", new { id }) == HttpStatusCode.NotFound, "Missing credential revocation reported success.");
                host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth.Permissions = [DashboardPermission.Read];
                Require((await client.GetAsync("/ops/api/passkeys")).StatusCode == HttpStatusCode.Forbidden, "Read permission exposed credential management.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
        await run("Passkeys: bounded challenges, session binding, invalid configuration, and credential limits", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "wafish-passkey-bounds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var clock = new Clock();
                using var sessions = new DashboardSessionStore(clock);
                var options = new DashboardOptions { Auth = new() { AuthMode = DashboardAuthMode.DefaultJwt, JwtSecret = password,
                    Passkeys = new() { Enabled = true, RelyingPartyId = "localhost", Origin = "https://localhost", StatePath = Path.Combine(directory, "keys.json") } } };
                DashboardPasskeyService Create() => new(Options.Create(options), sessions, clock);
                foreach (var origin in new[] { "http://example.com", "https://localhost/path", "https://other.invalid", "https://localhost?q=1", "https://user@localhost" })
                {
                    options.Auth.Passkeys.Origin = origin;
                    try { using var invalid = Create(); throw new Exception("Invalid passkey origin accepted."); } catch (ArgumentException) { }
                }
                options.Auth.Passkeys.Origin = "https://localhost";
                options.Auth.AuthMode = DashboardAuthMode.ApiKey;
                try { using var invalid = Create(); throw new Exception("API-key passkeys accepted."); } catch (ArgumentException) { }
                options.Auth.AuthMode = DashboardAuthMode.DefaultJwt;
                var path = options.Auth.Passkeys.StatePath;
                options.Auth.Passkeys.StatePath = "relative.json";
                try { using var invalid = Create(); throw new Exception("Relative passkey path accepted."); } catch (ArgumentException) { }
                options.Auth.Passkeys.StatePath = path;
                using var service = Create();
                Require(sessions.TryCreate("admin", password, sessions.Generation, out var session), "Fixture session failed.");
                foreach (var name in new[] { "", new string('x', 65), "control\nname" })
                {
                    try { await service.BeginAsync(name, session, default); throw new Exception("Invalid passkey name accepted."); } catch (ArgumentException) { }
                }
                try { await service.BeginAsync("name", "invalid", default); throw new Exception("Inactive enrollment session accepted."); } catch (ArgumentException) { }
                try { await service.BeginAsync(null, null, default); throw new Exception("Empty credential list enabled discovery."); } catch (InvalidOperationException) { }
                var ceremonies = new List<JsonElement>();
                for (var index = 0; index < DashboardPasskeyService.ChallengeCapacity; index++)
                    ceremonies.Add(JsonSerializer.SerializeToElement(await service.BeginAsync("key", session, default)));
                try { await service.BeginAsync("overflow", session, default); throw new Exception("Challenge capacity exceeded."); } catch (InvalidOperationException) { }
                clock.Advance(TimeSpan.FromMinutes(3));
                using var authenticator = new Authenticator();
                var expired = JsonSerializer.SerializeToElement(authenticator.Register(ceremonies[0]));
                try { await service.RegisterAsync(ceremonies[0].GetProperty("id").GetString()!, expired.GetProperty("credential").Deserialize<Fido2NetLib.AuthenticatorAttestationRawResponse>()!, session, default); throw new Exception("Expired enrollment succeeded."); } catch (ArgumentException) { }
                var rebound = JsonSerializer.SerializeToElement(await service.BeginAsync("key", session, default));
                Require(sessions.TryCreate("admin", password, sessions.Generation, out var peer), "Peer session failed.");
                var response = JsonSerializer.SerializeToElement(authenticator.Register(rebound)).GetProperty("credential").Deserialize<Fido2NetLib.AuthenticatorAttestationRawResponse>()!;
                try { await service.RegisterAsync(rebound.GetProperty("id").GetString()!, response, peer, default); throw new Exception("Enrollment moved between sessions."); } catch (ArgumentException) { }
                for (var index = 0; index < DashboardPasskeyService.CredentialCapacity; index++)
                {
                    using var device = new Authenticator();
                    var ceremony = JsonSerializer.SerializeToElement(await service.BeginAsync("key-" + index, session, default));
                    var credential = JsonSerializer.SerializeToElement(device.Register(ceremony)).GetProperty("credential").Deserialize<Fido2NetLib.AuthenticatorAttestationRawResponse>()!;
                    await service.RegisterAsync(ceremony.GetProperty("id").GetString()!, credential, session, default);
                }
                try { await service.BeginAsync("extra", session, default); throw new Exception("Credential capacity exceeded."); } catch (InvalidOperationException) { }
                Require((await service.ListAsync(default)).Length == DashboardPasskeyService.CredentialCapacity, "Bounded enrollment lost credentials.");
                using (var restored = Create()) Require((await restored.ListAsync(default)).Length == DashboardPasskeyService.CredentialCapacity, "Bounded state did not survive restart.");
                var committed = false;
                Require(!sessions.TryCommit("invalid", sessions.Generation, () => committed = true) && !committed, "Invalid session committed credential state.");
                var generation = sessions.Generation; sessions.InvalidateAll();
                Require(!sessions.TryCommit(session, generation, () => committed = true) && !committed, "Invalidated session committed credential state.");
                await File.WriteAllTextAsync(path, new string('x', 65537));
                try { using var invalid = Create(); throw new Exception("Oversized credential state accepted."); } catch (InvalidDataException) { }
                await File.WriteAllTextAsync(path, "{\"Fingerprint\":\"other\",\"Credentials\":[]}");
                try { using var invalid = Create(); throw new Exception("Reassigned credential state accepted."); } catch (InvalidDataException) { }
                options.Auth.Passkeys.Enabled = false;
                using var disabled = Create(); Require(!disabled.Enabled, "Passkeys enabled by default.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class Authenticator : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _id = RandomNumberGenerator.GetBytes(32);
        private string _user = "";
        public object Register(JsonElement ceremony)
        {
            var options = ceremony.GetProperty("publicKey"); _user = options.GetProperty("user").GetProperty("id").GetString()!;
            var key = _key.ExportParameters(false); var cose = new CborWriter();
            cose.WriteStartMap(5);
            cose.WriteInt32(1); cose.WriteInt32(2); cose.WriteInt32(3); cose.WriteInt32(-7);
            cose.WriteInt32(-1); cose.WriteInt32(1); cose.WriteInt32(-2); cose.WriteByteString(key.Q.X!);
            cose.WriteInt32(-3); cose.WriteByteString(key.Q.Y!); cose.WriteEndMap();
            var auth = Data(0x45, 0).Concat(new byte[16]).Concat(new byte[] { 0, 32 }).Concat(_id).Concat(cose.Encode()).ToArray();
            var attestation = new CborWriter(); attestation.WriteStartMap(3);
            attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
            attestation.WriteTextString("authData"); attestation.WriteByteString(auth);
            attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap(); attestation.WriteEndMap();
            return new { id = ceremony.GetProperty("id").GetString(), credential = new {
                id = Encode(_id), rawId = Encode(_id), type = "public-key", clientExtensionResults = new { },
                response = new { attestationObject = Encode(attestation.Encode()), clientDataJSON = Encode(Client("webauthn.create", options, "https://localhost")), transports = new[] { "internal" } }
            } };
        }
        public object Assert(JsonElement ceremony, uint counter, string origin = "https://localhost", bool corrupt = false)
        {
            var data = Client("webauthn.get", ceremony.GetProperty("publicKey"), origin); var auth = Data(5, counter);
            var signature = _key.SignData(auth.Concat(SHA256.HashData(data)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (corrupt) signature[^1] ^= 1;
            return new { id = ceremony.GetProperty("id").GetString(), credential = new {
                id = Encode(_id), rawId = Encode(_id), type = "public-key", clientExtensionResults = new { },
                response = new { authenticatorData = Encode(auth), clientDataJSON = Encode(data), signature = Encode(signature), userHandle = _user }
            } };
        }
        private static byte[] Data(byte flags, uint counter)
        {
            var result = new byte[37]; SHA256.HashData("localhost"u8).CopyTo(result, 0); result[32] = flags;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(33), counter); return result;
        }
        private static byte[] Client(string type, JsonElement options, string origin) => JsonSerializer.SerializeToUtf8Bytes(new { type, challenge = options.GetProperty("challenge").GetString(), origin, crossOrigin = false });
        private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public void Dispose() => _key.Dispose();
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
