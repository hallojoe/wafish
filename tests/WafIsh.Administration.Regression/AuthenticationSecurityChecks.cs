using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WafIsh.Dashboard.Infrastructure;
using WafIsh.Dashboard.Infrastructure.Auth;

internal static class AuthenticationSecurityChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run,
        Func<string, X509Certificate2, Task<WebApplication>> start, string password)
    {
        await run("Authentication: bounded atomic attempts, refunds, source isolation, and expiry", async () =>
        {
            var clock = new ManualClock();
            var limiter = new AuthenticationAttemptLimiter(clock);
            var accepted = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, 100), (i, ct) =>
            {
                var context = Context("192.0.2.1");
                context.Request.Headers["X-Forwarded-For"] = "198.51.100." + i;
                if (limiter.TryAcquire(context, "login", out _))
                {
                    Interlocked.Increment(ref accepted);
                }
                else
                {
                    Require(context.Response.StatusCode == 429 && context.Response.Headers.RetryAfter == "60", "Throttle response missing.");
                }

                return ValueTask.CompletedTask;
            });
            Require(accepted == AuthenticationAttemptLimiter.AttemptLimit, "Concurrent attempts exceeded the limit.");
            Require(limiter.TryAcquire(Context("192.0.2.2"), "login", out var refund), "Another source was blocked.");
            refund(); refund();
            clock.Advance(TimeSpan.FromMinutes(1));
            Require(limiter.TryAcquire(Context("192.0.2.1"), "login", out _), "Window did not expire.");
            for (var i = 0; i < 100; i++)
            {
                Require(limiter.TryAcquireAccount(Context("192.0.2.3")), "Account quota too small.");
            }

            Require(!limiter.TryAcquireAccount(Context("192.0.2.4")), "Distributed account attempts bypassed the limit.");
            var bounded = new AuthenticationAttemptLimiter(clock);
            for (var i = 0; i < AuthenticationAttemptLimiter.PartitionLimit; i++)
            {
                Require(bounded.TryAcquire(Context($"2001:db8::{i:x}"), "login", out _), "Premature capacity rejection.");
            }

            Require(!bounded.TryAcquire(Context("192.0.2.9"), "login", out _), "Partition capacity exceeded.");
            clock.Advance(TimeSpan.FromMinutes(1));
            Require(bounded.TryAcquire(Context("192.0.2.9"), "login", out _), "Expired partitions were retained.");
        });
        await run("Authentication: Basic and API-key failures throttle without limiting valid traffic", async () =>
        {
            foreach (var mode in new[] { DashboardAuthMode.BasicAuth, DashboardAuthMode.ApiKey })
            {
                var service = new DashboardAuthorizationService(Options.Create(new DashboardOptions
                {
                    Auth = new() { AuthMode = mode, ApiKey = password, JwtUsername = "admin", JwtPassword = password }
                }), NullLogger<DashboardAuthorizationService>.Instance);
                for (var i = 0; i < 50; i++)
                {
                    var valid = Context("192.0.2.1");
                    if (mode == DashboardAuthMode.ApiKey)
                    {
                        valid.Request.Headers["X-Api-Key"] = password;
                    }
                    else
                    {
                        valid.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + password));
                    }

                    Require(await service.IsAuthorizedAsync(valid), "Successful requests consumed the failure budget.");
                }
                for (var i = 0; i < AuthenticationAttemptLimiter.AttemptLimit; i++)
                {
                    Require(!await service.IsAuthorizedAsync(Context("192.0.2.1")), "Missing credentials passed.");
                }

                var blocked = Context("192.0.2.1");
                Require(!await service.IsAuthorizedAsync(blocked) && blocked.Response.StatusCode == 429, "Direct attempts were not throttled.");
            }
        });

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(20));
        await run("Authentication: HTTPS cookie login, logout, expiry, transport, host, and origin", async () =>
        {
            await using var host = await start("DefaultJwt", certificate);
            var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var https = new Uri(addresses.Single(a => a.StartsWith("https:")));
            var http = new Uri(addresses.Single(a => a.StartsWith("http:")));
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookies, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificate.GetCertHashString()
            };
            using var client = new HttpClient(handler) { BaseAddress = https, Timeout = TimeSpan.FromSeconds(10) };
            using (var login = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password }))
            {
                Require(login.IsSuccessStatusCode && cookies.GetCookies(https)["dashboard_token"] != null, "Login did not store a cookie.");
            }

            Require((await client.GetAsync("/ops/api/info")).StatusCode == HttpStatusCode.OK, "Cookie did not authenticate.");
            Require((await client.GetAsync(new Uri(http, "/ops/api/info"))).StatusCode == HttpStatusCode.Unauthorized, "Secure cookie reached HTTP.");
            Require(cookies.GetCookieHeader(new UriBuilder(https) { Host = "localhost" }.Uri) == "", "Cookie escaped its host.");
            using (var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/ops/api/info"))
            {
                crossOrigin.Headers.Add("Origin", "https://untrusted.invalid");
                Require((await client.SendAsync(crossOrigin)).StatusCode == HttpStatusCode.Forbidden, "Cross-origin cookie was accepted.");
            }
            using (var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/ops/api/info"))
            {
                sameOrigin.Headers.Add("Origin", https.GetLeftPart(UriPartial.Authority));
                Require((await client.SendAsync(sameOrigin)).IsSuccessStatusCode, "Same-origin cookie was rejected.");
            }
            using (var logout = await client.PostAsync("/ops/logout", null))
            {
                Require(logout.IsSuccessStatusCode, "Logout failed.");
            }

            Require((await client.GetAsync("/ops/api/info")).StatusCode == HttpStatusCode.Unauthorized, "Logout retained authentication.");
            cookies.Add(https, new Cookie("dashboard_token", ExpiredToken(), "/") { Secure = true, HttpOnly = true });
            Require((await client.GetAsync("/ops/api/info")).StatusCode == HttpStatusCode.Unauthorized, "Expired token was accepted.");
            await host.StopAsync();
        });
        await run("Authentication: HTTP login and direct credential limits return 429", async () =>
        {
            foreach (var mode in new[] { "DefaultJwt", "ApiKey", "BasicAuth", "Totp" })
            {
                await using var host = await start(mode == "Totp" ? "DefaultJwt" : mode, certificate);
                if (mode == "Totp")
                {
                    var auth = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
                    auth.EnableTwoFactor = true;
                    auth.TwoFactorSecret = TotpHelper.GenerateSecret();
                }
                var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(a => a.StartsWith("https:"));
                using var client = new HttpClient(new HttpClientHandler { UseCookies = false,
                    ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificate.GetCertHashString() }) { BaseAddress = new Uri(address) };
                for (var i = 0; i <= AuthenticationAttemptLimiter.AttemptLimit; i++)
                {
                    using var response = mode is "DefaultJwt" or "Totp"
                        ? await client.PostAsJsonAsync("/ops/login", new { username = "admin", password = mode == "Totp" ? password : "incorrect-password", twoFactorCode = "invalid" })
                        : await client.GetAsync("/ops/api/info");
                    if (i == AuthenticationAttemptLimiter.AttemptLimit)
                    {
                        Require(response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter != null, "Throttle status was overwritten.");
                    }

                    Require(!response.Headers.Contains("Set-Cookie"), "Failed attempts issued a cookie.");
                }
                await host.StopAsync();
            }
        });
    }
    private static string ExpiredToken()
    {
        static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var input = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")) + "." +
            Encode(JsonSerializer.SerializeToUtf8Bytes(new { sub = "admin", iss = "WafIsh.Dashboard", exp = 1 }));
        return input + "." + Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(new string('x', 64)), Encoding.ASCII.GetBytes(input)));
    }
    private static DefaultHttpContext Context(string ip)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip); return context;
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
    private static void Require(bool condition, string message) { if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
