using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Wafish.Infrastructure.Middleware;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Models;
using Wafish.Plugin.RateLimit.Redis;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class RedisDualWindowChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static RedisRateLimitExecutionConfig Policy() => new()
    { Algorithm = "DualWindow", Limit = 10, WindowSeconds = 120, BurstLimit = 5, BurstWindowSeconds = 30 };
    private static string Config(RedisRateLimitExecutionConfig policy) => JsonSerializer.Serialize(policy, Json);

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Dual quotas: compiler validates paired windows without changing legacy defaults", () =>
        {
            var compiler = new RedisRateLimitCompiler();
            CompiledRoutePlugin Compile(RedisRateLimitExecutionConfig config, int schema = 1) => compiler.Compile(
                new("binding", "rate-limit-redis", PluginBindingScope.Route, "route", Config(config), schema, 1, 0), new() { RouteId = "route" });
            Check(compiler.CanCompile("RATE-LIMIT-REDIS") && !compiler.CanCompile("rate-limit"), "Compiler selection differs.");
            Compile(Policy());
            Compile(new());
            Compile(new() { Algorithm = "TokenBucket", BurstBalance = 20 });
            foreach (var invalid in new[]
            {
                Policy() with { BurstLimit = null }, Policy() with { BurstWindowSeconds = null },
                Policy() with { BurstLimit = 0 }, Policy() with { BurstLimit = 1000001 },
                Policy() with { Limit = 0 }, Policy() with { Limit = 1000001 },
                Policy() with { BurstLimit = 11 }, Policy() with { BurstWindowSeconds = 0 },
                Policy() with { BurstWindowSeconds = 86401 }, Policy() with { BurstWindowSeconds = 120 },
                Policy() with { WindowSeconds = 86401 }, Policy() with { WindowSeconds = 0 },
                Policy() with { BurstBalance = 1 }, Policy() with { Algorithm = "FixedWindow" },
                Policy() with { Algorithm = "TokenBucket", BurstLimit = null }
            }) Reject(() => Compile(invalid));
            Reject(() => Compile(Policy(), 2));
            Reject(() => compiler.Compile(new("id", "rate-limit-redis", PluginBindingScope.Route, "route", "null", 1, 1, 0), new() { RouteId = "route" }));
            return Task.CompletedTask;
        });

        await run("Dual quotas: invalid active and disabled bindings fail before persistence", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start();
            var routes = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            var repository = fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>();
            var publisher = fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            Check((await routes.TryAddRouteConfig(new() { RouteId = "dual", Match = new() { Path = "/dual" } })).Success, "Route creation failed.");
            var before = publisher.Current;
            foreach (var enabled in new[] { true, false })
            {
                try { await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(new()
                    { PluginId = "rate-limit-redis", PluginVersion = "1.0", Scope = PluginBindingScope.Route, ScopeId = "dual", Enabled = enabled, ConfigJson = Config(Policy() with { BurstWindowSeconds = 120 }) }); throw new Exception("Invalid binding persisted."); }
                catch (ArgumentException) { }
                Check(! (await repository.GetBindingsAsync()).Any() && ReferenceEquals(before, publisher.Current), "Rejected quota changed stored or active state.");
            }
            await fixture.Bind("dual", "rate-limit-redis", Config(Policy()));
            Check((await repository.GetBindingsAsync()).Single().PluginId == "rate-limit-redis", "Valid quota failed to persist.");
        });

        await run("Dual quotas: middleware dispatches atomically and preserves headers and outage policy", async () =>
        {
            var store = new DualStub();
            var allowed = await Invoke(store, Policy());
            Check(allowed.Response.StatusCode == 200 && store.Calls == 1 && allowed.Response.Headers["X-RateLimit-Remaining"] == "4", "Combined admission was not used.");
            store.Result = new(false, 10, 0, 90);
            var denied = await Invoke(store, Policy() with { FailureMode = "FailOpen" });
            Check(denied.Response.StatusCode == 429 && denied.Response.Headers.RetryAfter == "90" && denied.Response.Headers["X-RateLimit-Limit"] == "10", "Rejection headers or FailOpen behavior differ.");
            store.Result = DistributedRateLimitResult.Unavailable(10);
            Check((await Invoke(store, Policy())).Response.StatusCode == 503, "Outage bypassed FailClosed.");
            Check((await Invoke(store, Policy() with { FailureMode = "FailOpen" })).Response.StatusCode == 200, "FailOpen did not apply.");
            Check((await Invoke(new LegacyStub(), Policy())).Response.StatusCode == 503, "Legacy store silently split combined admission.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await Invoke(store, Policy(), cancellation.Token); throw new Exception("Cancellation was ignored."); } catch (OperationCanceledException) { }
        });

        await run("Dual quotas: store sends one key and validates replies without replay", async () =>
        {
            var connection = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>();
            var database = (DatabaseProxy)((ConnectionProxy)connection).Database;
            await using var store = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance, null, TimeProvider.System, _ => Task.FromResult(connection));
            async Task<DistributedRateLimitResult> Acquire(long limit = 10, int window = 120, long burst = 5, int burstWindow = 30)
                => await store.TryAcquireDualWindowAsync("prefix:{route}:client", limit, window, burst, burstWindow, "test");
            Check((await Acquire()).Allowed && database.Keys.Length == 1 && database.Keys[0].ToString().Contains(":v3-dual:"), "Combined operation used multiple keys or legacy identity.");
            var key = database.Keys[0];
            Check((long)database.Values[1] == 120000 && (long)database.Values[3] == 30000, "Window conversion differs.");
            await Acquire(burst: 4); Check(key != database.Keys[0], "Burst change reused old quota.");
            await Acquire(burstWindow: 20); Check(key != database.Keys[0], "Burst window change reused old quota.");
            database.Reply = RedisResult.Create(new RedisValue[] { 0, 10, 0, 80 });
            Check((await Acquire()).RetryAfterSeconds == 80, "Combined retry result was not preserved.");
            database.Reply = RedisResult.Create(new RedisValue[] { 1, 9, 0 });
            Check(!(await Acquire()).Available, "Malformed reply was accepted.");
            var calls = database.Calls;
            foreach (var (limit, window, burst, burstWindow) in new[] { (0,120,5,30), (1000001,120,5,30), (10,86401,5,30), (10,120,0,30), (10,120,11,30), (10,120,5,0), (10,120,5,120) })
                Check(!(await Acquire(limit, window, burst, burstWindow)).Available, "Invalid quota reached Redis.");
            Check(database.Calls == calls, "Invalid quotas sent commands.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await store.TryAcquireDualWindowAsync("key", 10, 120, 5, 30, "test", cancellation.Token); throw new Exception("Cancelled store call succeeded."); } catch (OperationCanceledException) { }
            database.Fail = true;
            Check(!(await Acquire()).Available && database.Calls == calls + 1, "Failed script was retried or accepted.");
        });
    }

    public static async Task RunLiveAsync(RedisLuaRateLimitStore first, RedisLuaRateLimitStore second, string connectionString)
    {
        using var control = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var db = control.GetDatabase();
        var server = control.GetServer(control.GetEndPoints().Single());
        const string logical = "dual-live:{client}:route";
        Task<DistributedRateLimitResult> Acquire(RedisLuaRateLimitStore store) => store.TryAcquireDualWindowAsync(logical, 10, 120, 5, 30, null).AsTask();
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Acquire(i % 2 == 0 ? first : second)));
        Check(results.All(r => r.Available) && results.Count(r => r.Allowed) == 5, "Concurrent burst limit was exceeded.");
        var key = server.Keys(pattern: logical + ":*").Single();
        Check((long)await db.HashGetAsync(key, "sustainedCount") == 5 && (long)await db.HashGetAsync(key, "burstCount") == 5, "Burst rejection partially charged the sustained quota.");
        var ttl = await db.KeyTimeToLiveAsync(key);
        Check(ttl is { TotalSeconds: > 0 and <= 120 }, "Combined key expiry is missing or unbounded.");
        var before = await db.HashGetAllAsync(key);
        var denied = await Acquire(first);
        Check(!denied.Allowed && denied.Limit == 5 && denied.RetryAfterSeconds is > 0 and <= 30, "Burst rejection selected incorrect headers.");
        Check(Enumerable.SequenceEqual(before, await db.HashGetAllAsync(key)) && await db.KeyTimeToLiveAsync(key) <= ttl, "Rejected request mutated counters or extended expiry.");

        await db.HashSetAsync(key, "burstReset", 1); // Expire only the burst window, using the real Lua decision path.
        for (var i = 0; i < 5; i++) Check((await Acquire(i % 2 == 0 ? first : second)).Allowed, "Burst rollover lost sustained capacity.");
        denied = await Acquire(second);
        Check(!denied.Allowed && denied.Limit == 10 && denied.RetryAfterSeconds is > 30 and <= 120, "Both exhausted windows did not select the longest retry.");
        await db.HashSetAsync(key, "burstReset", 1);
        before = await db.HashGetAllAsync(key);
        denied = await Acquire(first);
        Check(!denied.Allowed && Enumerable.SequenceEqual(before, await db.HashGetAllAsync(key)), "Sustained rejection consumed or restarted the burst allowance.");
        await db.HashSetAsync(key, "sustainedReset", 1);
        Check((await Acquire(first)).Allowed && (long)await db.HashGetAsync(key, "sustainedCount") == 1 && (long)await db.HashGetAsync(key, "burstCount") == 1, "Both expired windows failed to restart.");
        await db.HashSetAsync(key, [new("sustainedCount", 9), new("burstReset", 1)]);
        var last = await Acquire(second);
        Check(last.Allowed && last.Limit == 10 && last.Remaining == 0, "Allowed headers did not select the tightest remaining allowance.");
        Check((await first.TryAcquireDualWindowAsync(logical, 11, 120, 5, 30, null)).Allowed, "Policy change reused previous counters.");
        Check((await first.TryAcquireAsync("FixedWindow", logical, 10, 120, 0, null)).Allowed, "Combined quota collided with legacy state.");
        await db.KeyDeleteAsync(key);
        Check((await Acquire(first)).Allowed, "Missing expired state did not restart both windows.");
        // Exercise actual Redis expiry with the smallest supported windows.
        var expiry = await first.TryAcquireDualWindowAsync("dual-expiry", 1, 2, 1, 1, null);
        Check(expiry.Allowed, "Expiry setup failed.");
        var expiryKey = server.Keys(pattern: "dual-expiry:*").Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await db.KeyExistsAsync(expiryKey)) await Task.Delay(20, timeout.Token);
        Check((await second.TryAcquireDualWindowAsync("dual-expiry", 1, 2, 1, 1, null)).Allowed, "Natural expiry did not restore allowance.");
        foreach (var prefix in new[] { "dual-deployment-a", "dual-deployment-b" })
        {
            var policy = Policy() with { KeyPrefix = prefix, BurstLimit = 1 };
            Check((await Invoke(first, policy)).Response.StatusCode == 200 && (await Invoke(second, policy)).Response.StatusCode == 429, "Middleware did not share quota within isolated deployment prefixes.");
        }
        Console.WriteLine("PASS: Live Redis dual quotas enforce both windows atomically across clients, rollover, expiry, headers, and prefixes");
    }

    public static async Task RunClusterAsync(string executable)
    {
        await using var cluster = new RedisFailureChecks.RedisServer(executable, cluster: true);
        await cluster.StartAsync();
        var endpoint = $"127.0.0.1:{cluster.Port}";
        using var control = await ConnectionMultiplexer.ConnectAsync(endpoint + ",allowAdmin=true,abortConnect=false");
        var server = control.GetServer("127.0.0.1", cluster.Port);
        await server.ExecuteAsync("CLUSTER", "ADDSLOTSRANGE", 0, 16383);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!(await server.ExecuteAsync("CLUSTER", "INFO")).ToString().Contains("cluster_state:ok"))
            await Task.Delay(20, timeout.Token);
        await using var first = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance);
        await using var second = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance);
        foreach (var key in new[] { "cluster:ordinary-key", "cluster:{tag}:client", "cluster:{different}:client" })
        {
            Check((await first.TryAcquireDualWindowAsync(key, 10, 60, 1, 5, endpoint)).Allowed, "Cluster rejected first combined permit.");
            var result = await second.TryAcquireDualWindowAsync(key, 10, 60, 1, 5, endpoint);
            Check(result.Available && !result.Allowed, "Cluster combined quota failed routing or sharing.");
        }
        Console.WriteLine("PASS: Live Redis Cluster admits both counters in one slot with ordinary and tagged keys");
    }

    private static async Task<DefaultHttpContext> Invoke(IDistributedRateLimitStore store, RedisRateLimitExecutionConfig policy, CancellationToken cancellation = default)
    {
        var route = new RouteConfig { RouteId = "dual-route", Match = new() { Path = "/dual" } };
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [route], RoutePlugins =
            ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                [new("binding", "rate-limit-redis", PluginBindingScope.Route, route.RouteId, Config(policy), 1, 1, 0)]) });
        var middleware = new RedisRateLimitMiddleware(c => { c.Items["forwarded"] = true; return Task.CompletedTask; },
            NullLogger<RedisRateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store,
            new(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
        var context = new DefaultHttpContext { RequestAborted = cancellation };
        context.Request.Path = "/dual"; context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        await using var body = new MemoryStream(); context.Response.Body = body;
        await middleware.InvokeAsync(context);
        Check(context.Items.ContainsKey("forwarded") == (context.Response.StatusCode == 200), "Denied request reached upstream.");
        return context;
    }
    private class LegacyStub : IDistributedRateLimitStore
    {
        public ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit, int windowSeconds, int burstBalance, string? redisConnectionString, CancellationToken cancellationToken = default)
            => throw new Exception("Combined middleware called the single-window API.");
    }
    private sealed class DualStub : LegacyStub, IDualWindowRateLimitStore
    {
        public int Calls;
        public DistributedRateLimitResult Result = new(true, 5, 4, 0);
        public ValueTask<DistributedRateLimitResult> TryAcquireDualWindowAsync(string key, long limit, int windowSeconds, long burstLimit, int burstWindowSeconds, string? redisConnectionString, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(Result); }
    }
    public class ConnectionProxy : DispatchProxy
    {
        public IDatabase Database = DispatchProxy.Create<IDatabase, DatabaseProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        { "GetDatabase" => Database, "DisposeAsync" => ValueTask.CompletedTask, _ => throw new NotSupportedException(method?.Name) };
    }
    public class DatabaseProxy : DispatchProxy
    {
        public RedisResult Reply = RedisResult.Create(new RedisValue[] { 1, 5, 4, 0 });
        public RedisKey[] Keys = [];
        public RedisValue[] Values = [];
        public int Calls;
        public bool Fail;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "ScriptEvaluateAsync") throw new NotSupportedException(method?.Name);
            Calls++; Keys = (RedisKey[])args![1]!; Values = (RedisValue[])args[2]!;
            return Fail ? Task.FromException<RedisResult>(new IOException("Injected outage")) : Task.FromResult(Reply);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); throw new Exception("Invalid policy accepted."); } catch (ArgumentException) { } }
}
