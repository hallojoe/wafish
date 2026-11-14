using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using System.Reflection;
using Grpc.Core;
using Grpc.Core.Interceptors;
using WafIsh.Controllers;
using WafIsh.Administration;
using WafIsh.Dashboard.Infrastructure;
using WafIsh.Dashboard.Infrastructure.Auth;
using WafIsh.Extensions;
using WafIsh.Models;
using WafIsh.Plugin.Cache;
using WafIsh.Services;
using WafIsh.Storage.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class SecurityChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Security: disabled registration removes REST and gRPC endpoints", async () =>
        {
            await using var app = CreateApp(false);
            app.MapControllers();
            app.MapWafIshGrpc();
            var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
            Check(!actions.OfType<ControllerActionDescriptor>().Any(a => a.ControllerTypeInfo.AsType() == typeof(GatewayConfigController)));
            Check(!((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
                .Any(e => e.DisplayName?.Contains("GatewayRegistry", StringComparison.Ordinal) == true));
        });
        await run("Security: enabled registration retains REST and gRPC endpoints", async () =>
        {
            await using var app = CreateApp(true);
            app.MapControllers();
            app.MapWafIshGrpc();
            var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
            Check(actions.OfType<ControllerActionDescriptor>().Any(a => a.ControllerTypeInfo.AsType() == typeof(GatewayConfigController)));
            Check(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
                .Any(e => e.DisplayName?.Contains("GatewayRegistry", StringComparison.Ordinal) == true));
        });
        await run("Security: empty dashboard API key denies requests", async () =>
        {
            foreach (var key in new string?[] { null, "", " " })
            {
                var auth = new DashboardAuthorizationService(Options.Create(new DashboardOptions
                { Auth = new() { AuthMode = DashboardAuthMode.ApiKey, ApiKey = key } }), NullLogger<DashboardAuthorizationService>.Instance);
                Check(!await auth.IsAuthorizedAsync(new DefaultHttpContext()));
            }
        });
        await run("Security: nested dashboard credentials configure gateway authentication", () =>
        {
            using var provider = AuthProvider(new()
            {
                ["Gateway:Dashboard:Auth:AuthMode"] = "DefaultJwt",
                ["Gateway:Dashboard:Auth:JwtPassword"] = "test-password",
                ["Gateway:Dashboard:Auth:JwtSecret"] = new string('s', 32)
            });
            var options = provider.GetRequiredService<IOptions<GatewayApiAuthOptions>>().Value;
            Check(options.Mode == GatewayApiAuthMode.Jwt && options.JwtSecret == new string('s', 32));
            return Task.CompletedTask;
        });
        await run("Security: explicit control-plane authentication wins over legacy settings", () =>
        {
            using var provider = AuthProvider(new()
            {
                ["Gateway:Security:ControlPlane:AuthMode"] = "ApiKey",
                ["Gateway:Security:ControlPlane:ApiKey"] = "test-key",
                ["Gateway:ApiAuth:Mode"] = "None"
            });
            Check(provider.GetRequiredService<IOptions<GatewayApiAuthOptions>>().Value.Mode == GatewayApiAuthMode.ApiKey);
            return Task.CompletedTask;
        });
        await run("Security: invalid gateway credentials and modes are rejected", () =>
        {
            foreach (var settings in new Dictionary<string, string?>[]
            {
                new() { ["Gateway:Security:ControlPlane:AuthMode"] = "ApiKey" },
                new() { ["Gateway:Security:ControlPlane:AuthMode"] = "BasicAuth", ["Gateway:Security:ControlPlane:Username"] = "admin" },
                new() { ["Gateway:Security:ControlPlane:AuthMode"] = "misspelled" },
                new() { ["Gateway:Security:ControlPlane:AuthMode"] = "99" }
            })
            {
                using var provider = AuthProvider(settings);
                try { _ = provider.GetRequiredService<IOptions<GatewayApiAuthOptions>>().Value; }
                catch (OptionsValidationException) { continue; }
                throw new InvalidOperationException("Unsafe gateway authentication configuration was accepted.");
            }
            return Task.CompletedTask;
        });
        await run("Security: gRPC accepts only credentials for its configured mode", async () =>
        {
            foreach (var mode in new[] { GatewayApiAuthMode.ApiKey, GatewayApiAuthMode.BasicAuth, GatewayApiAuthMode.Jwt })
            {
                var services = new ServiceCollection().AddLogging();
                services.AddSingleton<IOptions<GatewayApiAuthOptions>>(Options.Create(new GatewayApiAuthOptions
                { Mode = mode, ApiKey = "test-key", Username = "admin", Password = "test-password", JwtSecret = new string('s', 32) }));
                services.AddSingleton(new ControlPlaneAccessPolicy(Options.Create(new ControlPlaneSecurityOptions())));
                using var provider = services.BuildServiceProvider();
                var type = typeof(GatewaySnapshot).Assembly.GetType("WafIsh.Services.GrpcAuthInterceptor", true)!;
                var interceptor = (Interceptor)ActivatorUtilities.CreateInstance(provider, type);
                foreach (var credential in new[] { "missing", "wrong", "key", "basic", "bearer", "jwt" })
                {
                    var headers = new Metadata();
                    if (credential == "key") headers.Add("x-api-key", "test-key");
                    if (credential == "wrong") headers.Add("x-api-key", "wrong");
                    if (credential == "basic") headers.Add("authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:test-password")));
                    if (credential == "bearer") headers.Add("authorization", "Bearer test-password");
                    if (credential == "jwt") headers.Add("authorization", "Bearer " + DashboardJwtHelper.GenerateToken("admin", new string('s', 32)));
                    var expected = mode switch { GatewayApiAuthMode.ApiKey => credential == "key", GatewayApiAuthMode.BasicAuth => credential == "basic", _ => credential == "jwt" };
                    var called = false;
                    try
                    {
                        await interceptor.UnaryServerHandler(new object(), new CallContext(headers), (_, _) =>
                        { called = true; return Task.FromResult(new object()); });
                        Check(expected && called);
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
                    { Check(!expected && !called); }
                }
            }
        });
        foreach (var directive in new[] { "private", "no-store", "private=\"Authorization\"" })
            await run($"Security: cache excludes {directive} responses", async () =>
            {
                using var fixture = new CacheFixture(directive);
                Check(await fixture.RequestAsync("Alice") == "Alice");
                Check(await fixture.RequestAsync("Bob") == "Bob");
                Check(fixture.UpstreamCalls == 2);
            });
        await run("Security: Set-Cookie responses are not cached", async () =>
        {
            using var fixture = new CacheFixture("public", setCookie: true);
            Check(await fixture.RequestAsync("Alice") == "Alice");
            Check(await fixture.RequestAsync("Bob") == "Bob");
        });
        foreach (var identity in new[] { "authorization", "cookie", "principal" })
            await run($"Security: {identity} bypasses cache reads and writes", async () =>
            {
                using var fixture = new CacheFixture("public");
                Check(await fixture.RequestAsync("Alice", identity) == "Alice");
                Check(await fixture.RequestAsync("public") == "public");
                Check(await fixture.RequestAsync("Bob", identity) == "Bob");
                Check(await fixture.RequestAsync("unused") == "public");
                Check(fixture.UpstreamCalls == 3);
            });
        await run("Security: eligible public responses remain cached", async () =>
        {
            using var fixture = new CacheFixture("public");
            Check(await fixture.RequestAsync("public") == "public");
            Check(await fixture.RequestAsync("unused") == "public");
            Check(fixture.UpstreamCalls == 1);
        });
    }

    private static WebApplication CreateApp(bool registration)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Security:ControlPlane:AuthMode"] = "ApiKey",
            ["Gateway:Security:ControlPlane:ApiKey"] = "test-key"
        });
        builder.Services.AddWafIsh(enableRegistration: registration);
        return builder.Build();
    }

    private static ServiceProvider AuthProvider(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddGatewayApiAuth();
        return services.BuildServiceProvider();
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Security assertion failed.");
    }

    private sealed class CallContext : ServerCallContext
    {
        private readonly Metadata headers;
        public CallContext(Metadata headers)
        {
            this.headers = headers;
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            context.Connection.LocalIpAddress = System.Net.IPAddress.Loopback;
            context.Request.Host = new HostString("localhost");
            context.Request.Scheme = "http";
            UserState["__HttpContext"] = context;
        }
        protected override string MethodCore => "/registry/test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:1234";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("test", new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }

    private sealed class CacheFixture : IDisposable
    {
        private readonly ResponseCacheStore _cache = new(Options.Create(new ResponseCacheBudgetOptions()));
        private readonly ResponseCacheMiddleware _middleware;
        private readonly RouteConfig _route = new() { RouteId = "test-route", Match = new() { Path = "/test" } };
        public int UpstreamCalls { get; private set; }

        public CacheFixture(string directive, bool setCookie = false)
        {
            var publisher = new GatewaySnapshotPublisher();
            publisher.Publish(GatewaySnapshot.Empty with
            {
                Version = 1,
                Routes = [_route],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("test-route",
                    [new("binding", "response-cache", PluginBindingScope.Route, "test-route", "{\"enabled\":true}", 1, 1, 0)])
            });
            var plans = new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            _middleware = new ResponseCacheMiddleware(async context =>
            {
                UpstreamCalls++;
                context.Response.Headers.CacheControl = directive;
                if (setCookie) context.Response.Headers.SetCookie = "session=upstream";
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes((string)context.Items["body"]!));
            }, _cache, plans);
        }

        public async Task<string> RequestAsync(string body, string? identity = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/test";
            context.Request.Host = new HostString("localhost");
            context.Items["body"] = body;
            if (identity == "authorization") context.Request.Headers.Authorization = "Bearer " + body;
            if (identity == "cookie") context.Request.Headers.Cookie = "session=" + body;
            if (identity == "principal") context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, body)], "test"));
            context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(_route, null!, HttpTransformer.Default) });
            await using var output = new MemoryStream();
            context.Response.Body = output;
            await _middleware.InvokeAsync(context);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public void Dispose() { }
    }
}
