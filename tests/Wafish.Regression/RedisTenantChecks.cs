using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Infrastructure.Middleware;
using Wafish.Models;
using Wafish.Plugin.RateLimit.Redis;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static partial class RedisTenantChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static RedisTenantPartitionOptions Tenants() => new() { ClaimType = "tenant_id", ClaimIssuer = "https://issuer.test", AuthenticationType = "verified-jwt", AllowedTenants = ["alpha", "beta"] };
    private static RedisRateLimitExecutionConfig Policy() => new() { Limit = 2, WindowSeconds = 120, TenantPartition = Tenants() };
    private static string Config(RedisRateLimitExecutionConfig config) => JsonSerializer.Serialize(config, Json);
    private static Claim Claim(string value, string issuer = "https://issuer.test", string? originalIssuer = null, string valueType = ClaimValueTypes.String)
        => new("tenant_id", value, valueType, issuer, originalIssuer ?? issuer);
    private static ClaimsPrincipal User(string value = "alpha", string? authentication = "verified-jwt", string issuer = "https://issuer.test")
        => new(new ClaimsIdentity([Claim(value, issuer)], authentication));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid tenant policy accepted."); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Tenant quotas: compilation bounds trust, provisioning, immutable keys, and schema", () =>
        {
            RedisRateLimitExecutionConfig Compile(RedisRateLimitExecutionConfig config, string route = "route") => RedisTenantPartitionPolicy.Compile(config, route);
            Check(Compile(new()).CompiledTenantPartition is null, "Default IP partition changed.");
            var options = Tenants(); var compiled = Compile(Policy() with { TenantPartition = options }).CompiledTenantPartition!;
            options.AllowedTenants[0] = "tampered";
            Check(compiled.TenantKeys.ContainsKey("alpha") && !compiled.TenantKeys.ContainsKey("tampered"), "Caller changed compiled tenants.");
            var reordered = Compile(Policy() with { TenantPartition = Tenants() with { AllowedTenants = ["beta", "alpha", "gamma"] } }).CompiledTenantPartition!;
            Check(compiled.TenantKeys["alpha"] == reordered.TenantKeys["alpha"], "Provisioning another tenant reset existing quotas.");
            Check(compiled.TenantKeys["alpha"] != Compile(Policy(), "other-route").CompiledTenantPartition!.TenantKeys["alpha"], "Routes shared tenant identity.");
            foreach (var options2 in new[] { Tenants() with { ClaimType = "other" }, Tenants() with { ClaimIssuer = "other" }, Tenants() with { AuthenticationType = "other" } })
                Check(compiled.TenantKeys["alpha"] != Compile(Policy() with { TenantPartition = options2 }).CompiledTenantPartition!.TenantKeys["alpha"], "Trust changes reused old tenant keys.");
            foreach (var invalid in new[] { Tenants() with { ClaimType = "" }, Tenants() with { ClaimType = new string('x', 257) },
                Tenants() with { ClaimIssuer = "issuer\n" }, Tenants() with { ClaimIssuer = new string('x', 513) },
                Tenants() with { AuthenticationType = " " }, Tenants() with { AuthenticationType = new string('x', 129) },
                Tenants() with { AllowedTenants = [] }, Tenants() with { AllowedTenants = null! },
                Tenants() with { AllowedTenants = ["alpha", "alpha"] }, Tenants() with { AllowedTenants = [null!] },
                Tenants() with { AllowedTenants = [" alpha"] }, Tenants() with { AllowedTenants = ["a\0b"] },
                Tenants() with { AllowedTenants = [new string('x', 129)] },
                Tenants() with { AllowedTenants = Enumerable.Range(0, 1025).Select(i => i.ToString()).ToArray() } }) Reject(() => Compile(Policy() with { TenantPartition = invalid }));
            Check(Compile(Policy() with { TenantPartition = Tenants() with { AllowedTenants = Enumerable.Range(0, 1024).Select(i => i.ToString()).ToArray() } }).CompiledTenantPartition!.TenantKeys.Count == 1024, "Maximum allowlist rejected.");
            foreach (var invalid in new[] { Policy() with { Limit = 0 }, Policy() with { Limit = 1000001 }, Policy() with { WindowSeconds = 0 },
                Policy() with { WindowSeconds = 86401 }, Policy() with { BurstBalance = -1 }, Policy() with { BurstBalance = 1000001 }, Policy() with { BurstBalance = 1 }, Policy() with { Algorithm = "TokenBucket", BurstBalance = 3 } }) Reject(() => Compile(invalid));
            Compile(Policy() with { Algorithm = "TokenBucket", BurstBalance = 2 });
            var serialized = Config(Compile(Policy())); Check(!serialized.Contains("compiledTenantPartition", StringComparison.OrdinalIgnoreCase), "Derived trust keys were serialized.");
            var compiler = new RedisRateLimitCompiler();
            Reject(() => compiler.Compile(new("b", "rate-limit-redis", PluginBindingScope.Route, "route", Config(Policy()), 2, 1, 0), new() { RouteId = "route" }));
            var schema = new RedisRateLimitPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
            var validator = new PluginConfigurationSchemaValidator();
            Check(validator.TryValidate(Config(Policy()), schema, out _, out var error), error);
            Check(validator.TryValidate("{\"tenantPartition\":null}", schema, out _, out error), error);
            Check(!validator.TryValidate("{\"tenantPartition\":{\"claimType\":\"tenant\"}}", schema, out _, out _), "Incomplete tenant schema accepted.");
            return Task.CompletedTask;
        });
        await run("Tenant quotas: anonymous, forged, ambiguous, unknown, and excessive identities cannot reach Redis", async () =>
        {
            var store = new Store();
            var invalid = new[] {
                (new ClaimsPrincipal(), 401), (User(authentication: null), 401), (User(authentication: "untrusted"), 403),
                (User(issuer: "untrusted"), 403), (User("unknown"), 403), (User("Alpha"), 403),
                (new ClaimsPrincipal(new ClaimsIdentity([], "verified-jwt")), 403),
                (new ClaimsPrincipal(new ClaimsIdentity([Claim("alpha"), Claim("beta")], "verified-jwt")), 403),
                (new ClaimsPrincipal(new ClaimsIdentity([Claim("alpha"), Claim("alpha")], "verified-jwt")), 403),
                (new ClaimsPrincipal(new ClaimsIdentity([Claim("alpha", originalIssuer: "untrusted")], "verified-jwt")), 403),
                (new ClaimsPrincipal(new ClaimsIdentity([Claim("alpha", valueType: ClaimValueTypes.Integer)], "verified-jwt")), 403),
                (User(new string('x', 129)), 403),
                (new ClaimsPrincipal(Enumerable.Range(0, 17).Select(_ => new ClaimsIdentity())), 403),
                (new ClaimsPrincipal(new ClaimsIdentity(Enumerable.Range(0, 257).Select(i => new Claim("other", i.ToString())), "verified-jwt")), 403)
            };
            foreach (var (principal, expected) in invalid)
            {
                var response = await Invoke(store, Policy() with { FailureMode = "FailOpen" }, principal);
                Check(response.Response.StatusCode == expected && response.Response.Headers.CacheControl == "no-store", "Invalid identity returned " + response.Response.StatusCode);
            }
            for (var i = 0; i < 100; i++) Check((await Invoke(store, Policy(), User("unprovisioned-" + i))).Response.StatusCode == 403, "Unknown tenant accepted.");
            Check(store.Calls == 0 && store.Counts.Count == 0, "Invalid identities created quota keys.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await Invoke(store, Policy(), User(), cancellation: cancellation.Token); throw new Exception("Cancelled request reached Redis."); } catch (OperationCanceledException) { }
        });
        await run("Tenant quotas: users and addresses share one allowance while tenant, route, prefix, and IP quotas stay separate", async () =>
        {
            var store = new Store();
            var user = User(); user.AddIdentity(new ClaimsIdentity([Claim("beta")], "untrusted"));
            Check((await Invoke(store, Policy(), user)).Response.StatusCode == 200, "Untrusted extra identity replaced trusted tenant.");
            Check((await Invoke(store, Policy(), User(), address: "192.0.2.2")).Response.StatusCode == 200, "Another address did not share remaining capacity.");
            var denied = await Invoke(store, Policy(), User(), address: "192.0.2.3");
            Check(denied.Response.StatusCode == 429 && denied.Response.Headers.RetryAfter == "120", "Tenant exhaustion lost retry guidance.");
            foreach (var request in new[] { (Policy(), User("beta"), "route"), (Policy(), User(), "another-route"), (Policy() with { KeyPrefix = "separate" }, User(), "route") })
                Check((await Invoke(store, request.Item1, request.Item2, routeId: request.Item3)).Response.StatusCode == 200, "Tenant quota leaked across isolation boundary.");
            var legacy = Policy() with { TenantPartition = null };
            Check((await Invoke(store, legacy, new())).Response.StatusCode == 200 && store.Counts.ContainsKey("wafish:rl:route:127.0.0.1"), "Legacy IP key changed.");
            Check(store.Counts.Keys.Where(key => key.Contains(":tenant:v1:")).All(key => !key.Contains("alpha") && !key.Contains("beta")), "Keys exposed raw tenant values.");
            store.Unavailable = true;
            Check((await Invoke(store, Policy(), User())).Response.StatusCode == 503, "Trusted tenant bypassed FailClosed outage.");
            Check((await Invoke(store, Policy() with { FailureMode = "FailOpen" }, User())).Response.StatusCode == 200, "Trusted tenant ignored FailOpen outage.");
        });
        await run("Tenant quotas: invalid enabled and disabled bindings preserve stored and active configuration", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start();
            var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            var repository = fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var publisher = fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            Check((await routes.TryAddRouteConfig(new() { RouteId = "tenant", Match = new() { Path = "/tenant" } })).Success, "Route creation failed.");
            var before = publisher.Current;
            foreach (var enabled in new[] { true, false })
            foreach (var invalid in new[] { Policy() with { TenantPartition = Tenants() with { AllowedTenants = ["alpha", "alpha"] } },
                Policy() with { TenantPartition = Tenants() with { ClaimIssuer = " https://issuer.test" } }, Policy() with { WindowSeconds = 86401 } })
            {
                try { await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(new()
                { PluginId = "rate-limit-redis", PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "tenant", Enabled = enabled,
                    ConfigJson = Config(invalid) }); throw new Exception("Invalid binding persisted."); }
                catch (ArgumentException) { }
                Check(!(await repository.GetBindingsAsync()).Any() && ReferenceEquals(before, publisher.Current), "Invalid tenant policy changed state.");
            }
            await fixture.Bind("tenant", "rate-limit-redis", Config(Policy()));
            Check((await repository.GetBindingsAsync()).Single().PluginId == "rate-limit-redis", "Valid tenant policy did not persist.");
        });
        await run("Tenant quotas: authenticated HTTP requests enforce tenant identity before outage fallback", HttpAsync);
    }

    private static async Task HttpAsync()
    {
        using var unavailable = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Own the unavailable endpoint without accepting connections.
        await using var fixture = await RedirectChecks.Fixture.Start(services =>
            services.AddAuthentication("claims-test").AddScheme<AuthenticationSchemeOptions, ClaimsTransformChecks.TestAuthentication>("claims-test", _ => { }));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var backend = builder.Build(); var calls = 0;
        backend.MapGet("/tenant", () => { Interlocked.Increment(ref calls); return "tenant upstream"; }); await backend.StartAsync();
        var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
        Check((await routes.TryAddClusterConfig(new() { ClusterId = "tenant", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = backend.Urls.Single() } } })).Success, "Cluster creation failed.");
        Check((await routes.TryAddRouteConfig(new() { RouteId = "tenant", ClusterId = "tenant", Match = new() { Path = "/tenant" } })).Success, "Route creation failed.");
        await fixture.Bind("tenant", "rate-limit-redis", Config(Policy() with { FailureMode = "FailOpen", RedisConnectionString = unavailable.LocalEndPoint + ",abortConnect=true,connectTimeout=100,connectRetry=0,syncTimeout=100", TenantPartition = Tenants() with { AuthenticationType = "claims-test", ClaimIssuer = ClaimsIdentity.DefaultIssuer } }));
        await fixture.WaitRoute("tenant");
        using (var anonymous = await fixture.Client.GetAsync("/tenant?tenant_id=alpha")) Check(anonymous.StatusCode == HttpStatusCode.Unauthorized && calls == 0, "Query tenant bypassed authentication.");
        foreach (var (identity, status) in new[] { ("missing", HttpStatusCode.Forbidden), ("one", HttpStatusCode.OK), ("two", HttpStatusCode.OK) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/tenant"); request.Headers.Add("X-Test-Identity", identity);
            using var response = await fixture.Client.SendAsync(request); Check(response.StatusCode == status, "Authenticated HTTP quota returned " + response.StatusCode);
        }
        Check(calls == 2, "Tenant rejection reached upstream or trusted outage fallback failed."); await backend.StopAsync();
    }

    internal static async Task<DefaultHttpContext> Invoke(IDistributedRateLimitStore store, RedisRateLimitExecutionConfig policy, ClaimsPrincipal user,
        string address = "127.0.0.1", string routeId = "route", CancellationToken cancellation = default)
    {
        var route = new RouteConfig { RouteId = routeId, Match = new() { Path = "/tenant" } }; var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route], RoutePlugins =
            ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(routeId,
                [new("binding", "rate-limit-redis", PluginBindingScope.Route, routeId, Config(policy), 1, 1, 0)]) });
        var middleware = new RedisRateLimitMiddleware(c => { c.Items["forwarded"] = true; return Task.CompletedTask; },
            NullLogger<RedisRateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store,
            new(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
        var context = new DefaultHttpContext { User = user, RequestAborted = cancellation };
        context.Request.Headers["X-Tenant-ID"] = "beta"; context.Request.QueryString = new("?tenant_id=beta");
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        await using var body = new MemoryStream(); context.Response.Body = body;
        await middleware.InvokeAsync(context);
        Check(context.Items.ContainsKey("forwarded") == (context.Response.StatusCode == 200), "Denied tenant reached upstream.");
        return context;
    }
    private sealed class Store : IDistributedRateLimitStore
    {
        internal readonly ConcurrentDictionary<string, int> Counts = new();
        internal int Calls; internal bool Unavailable;
        public ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit, int windowSeconds, int burstBalance,
            string? redisConnectionString, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Unavailable) return ValueTask.FromResult(DistributedRateLimitResult.Unavailable(limit));
            var count = Counts.AddOrUpdate(key, 1, (_, previous) => previous + 1);
            return ValueTask.FromResult(new DistributedRateLimitResult(count <= limit, limit, Math.Max(0, limit - count), count > limit ? windowSeconds : 0));
        }
    }
}
