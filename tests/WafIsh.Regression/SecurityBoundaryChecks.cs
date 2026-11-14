using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Models;
using Wafish.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static class SecurityBoundaryChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Security: network policy rejects remote HTTP and anonymous forwarding", () =>
        {
            var normal = new ControlPlaneAccessPolicy(Options.Create(new ControlPlaneSecurityOptions()));
            var local = new ControlPlaneAccessPolicy(Options.Create(new ControlPlaneSecurityOptions { AllowAnonymousLoopback = true }));
            var context = Context();
            Check(!normal.IsAllowed(context, anonymous: true));
            Check(normal.IsAllowed(context, anonymous: false));
            Check(local.IsAllowed(context, anonymous: true));
            context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
            Check(!local.IsAllowed(context, anonymous: true));
            context.Request.Headers.Clear();
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            Check(!local.IsAllowed(context, anonymous: true));
            Check(!normal.IsAllowed(context, anonymous: false));
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            Check(!normal.IsAllowed(context, anonymous: false));
            context.Request.Scheme = "https";
            Check(normal.IsAllowed(context, anonymous: false));
            Check(!local.IsAllowed(context, anonymous: true));
            context = Context();
            context.Request.Host = new HostString("attacker.example");
            Check(!local.IsAllowed(context, anonymous: true));
            context = Context();
            context.Request.Headers.Origin = "https://attacker.example";
            Check(!local.IsAllowed(context, anonymous: true));
            return Task.CompletedTask;
        });
        await run("Security: JWT requires algorithm, issuer, subject, signature, and expiry", () =>
        {
            var secret = new string('s', 32);
            Check(DashboardJwtHelper.ValidateToken(DashboardJwtHelper.GenerateToken("operator", secret), secret).Valid);
            var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
            foreach (var payload in new object[]
            {
                new { sub = "operator", iss = "WafIsh.Dashboard" },
                new { sub = "operator", iss = "wrong", exp = expiry },
                new { sub = "", iss = "WafIsh.Dashboard", exp = expiry },
                new { sub = "operator", iss = "WafIsh.Dashboard", exp = 0 },
                new { sub = "operator", iss = "WafIsh.Dashboard", exp = "bad" }
            })
            {
                Check(!DashboardJwtHelper.ValidateToken(Token(payload, secret), secret).Valid);
            }

            Check(!DashboardJwtHelper.ValidateToken(Token(new { sub = "operator", iss = "WafIsh.Dashboard", exp = expiry }, secret, "none"), secret).Valid);
            Check(!DashboardJwtHelper.ValidateToken(DashboardJwtHelper.GenerateToken("operator", secret), new string('x', 32)).Valid);
            return Task.CompletedTask;
        });
        await run("Security: query JWT matches only the two exact hub paths", async () =>
        {
            var secret = new string('s', 32);
            var service = new DashboardAuthorizationService(Options.Create(new DashboardOptions
            { RoutePrefix = "ops", Auth = new() { AuthMode = DashboardAuthMode.DefaultJwt, JwtSecret = secret } }),
                NullLogger<DashboardAuthorizationService>.Instance);
            foreach (var path in new[] { "/ops/hubs/traffic", "/ops/hubs/overview", "/ops/hubs/overview/extra", "/other/hubs/overview", "/ops/api/hubs/overview" })
            {
                var context = Context();
                context.Request.Path = path;
                context.Request.QueryString = new QueryString("?access_token=" + DashboardJwtHelper.GenerateToken("operator", secret));
                Check(await service.IsAuthorizedAsync(context) == (path is "/ops/hubs/traffic" or "/ops/hubs/overview"));
            }
        });
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost");
        context.Request.Scheme = "http";
        return context;
    }
    private static string Token(object payload, string secret, string algorithm = "HS256")
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var input = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm, typ = "JWT" })) + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return input + "." + Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(input)));
    }
    private static void Check(bool value) { if (!value)
        {
            throw new Exception("Security boundary assertion failed.");
        }
    }
}
