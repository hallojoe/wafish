using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using StackExchange.Redis;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugin.RateLimit.Redis;
using Wafish.Services;
using Wafish.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static partial class RedisFailureChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Redis: combined quotas validate settings and preserve unsupported-store outage policy", CombinedPolicyAsync);
        await run("Rate limiting: exhausted permits return JSON and Retry-After without forwarding", async () =>
        {
            var route = new RouteConfig { RouteId = "local-limit", Match = new() { Path = "/local-limit" } };
            var snapshots = new GatewaySnapshotPublisher();
            snapshots.Publish(GatewaySnapshot.Empty with
            {
                Version = 1, Routes = [route],
                RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                    [new("local-binding", "rate-limit", PluginBindingScope.Route, route.RouteId,
                        "{\"enabled\":true,\"algorithm\":\"FixedWindow\",\"permitLimit\":1,\"window\":\"00:01:00\",\"queueLimit\":0}", 1, 1, 0)])
            });
            var plans = new GatewayPluginExecutionPlanProvider(snapshots, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
            var store = new Wafish.Dashboard.Infrastructure.State.InMemoryRateLimiterStore();
            var forwarded = 0;
            var middleware = new Wafish.Plugin.RateLimit.RateLimitMiddleware(_ => { forwarded++; return Task.CompletedTask; },
                NullLogger<Wafish.Plugin.RateLimit.RateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store, plans);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var context = new DefaultHttpContext();
                    context.Request.Path = "/local-limit";
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                    context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
                    await using var body = new MemoryStream(); context.Response.Body = body;
                    await middleware.InvokeAsync(context);
                    if (attempt == 0)
                    {
                        Check(context.Response.StatusCode == 200, "First permit was rejected.");
                    }
                    else
                    {
                        Check(context.Response.StatusCode == 429 && context.Response.Headers.RetryAfter == "60", "Exhausted limit lost status or retry guidance.");
                        using var json = JsonDocument.Parse(body.ToArray());
                        Check(json.RootElement.GetProperty("retryAfter").GetInt32() == 60, "Error response lost retry timing.");
                    }
                }
                Check(forwarded == 1, "Rejected request reached upstream.");
            }
            finally { store.Cleanup(TimeSpan.Zero, 0); }
        });

        await run("Redis: route failure policy distinguishes outages from exhausted limits", async () =>
        {
            var store = new StubStore { Result = DistributedRateLimitResult.Unavailable(2) };
            Check(await Invoke(store, "FailClosed") == 503, "FailClosed allowed an outage request.");
            Check(await Invoke(store, "FailOpen") == 200, "FailOpen rejected an outage request.");
            store.Result = new(false, 2, 0, 30);
            Check(await Invoke(store, "FailOpen") == 429, "FailOpen bypassed a real limit.");
            store.Result = new(true, 2, 1, 0);
            Check(await Invoke(store, "FailClosed") == 200, "Recovery did not resume forwarding.");
            store.Throw = true;
            Check(await Invoke(store, "FailClosed") == 503 && await Invoke(store, "FailOpen") == 200,
                "Store exceptions bypassed the configured failure policy.");
        });
    }

    public static async Task RunLiveAsync()
    {
        var executable = Environment.GetEnvironmentVariable("WAFISH_REDIS_SERVER")
            ?? throw new InvalidOperationException("Set WAFISH_REDIS_SERVER to a Redis server executable.");
        await using var server = new RedisServer(executable);
        await server.StartAsync();
        var options = Options.Create(new RedisRateLimitStoreOptions { ConnectionString = $"127.0.0.1:{server.Port}" });
        await using var first = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance, options);
        await using var second = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance, options);
        await CombinedLiveAsync(first, second, options.Value.ConnectionString!);
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket" })
        {
            var key = "test:" + algorithm;
            var capacity = algorithm == "TokenBucket" ? 3 : 2;
            for (var i = 0; i < capacity; i++)
            {
                var result = await (i % 2 == 0 ? first : second).TryAcquireAsync(algorithm, key, 2, 30, 1, null);
                Check(result.Available && result.Allowed && result.Remaining == capacity - i - 1, algorithm + " counter was incorrect.");
            }
            var denied = await second.TryAcquireAsync(algorithm, key, 2, 30, 1, null);
            Check(denied.Available && !denied.Allowed && denied.RetryAfterSeconds is > 0 and <= 30,
                algorithm + " did not enforce its shared limit.");
        }
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket" })
        {
            var result = await first.TryAcquireAsync(algorithm, "shared-policy-key", 2, 30, 1, null);
            Check(result.Available && result.Allowed, "Algorithm change reused an incompatible Redis value.");
            var changed = await second.TryAcquireAsync(algorithm, "shared-policy-key", 3, 30, 1, null);
            Check(changed.Available && changed.Allowed && changed.Remaining == (algorithm == "TokenBucket" ? 3 : 2), "Policy change reused old counters.");
        }
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket" })
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i =>
                (i % 2 == 0 ? first : second).TryAcquireAsync(algorithm, "concurrent:" + algorithm, 20, 30, 1, null).AsTask()));
            Check(results.All(r => r.Available) && results.Count(r => r.Allowed) == (algorithm == "TokenBucket" ? 21 : 20),
                algorithm + " exceeded capacity under concurrent callers.");
        }
        Console.WriteLine("PASS: Live Redis isolates algorithm and policy changes on the same logical key");
        Console.WriteLine("PASS: Live Redis shares fixed-window, sliding-window, and token-bucket counters across two clients");
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket" })
        {
            var prefix = "deployment-a:" + algorithm;
            Check(await Invoke(first, "FailClosed", prefix, algorithm) == 200, "Deployment A rejected its first request.");
            Check(await Invoke(second, "FailClosed", prefix, algorithm) == 200, "Replica did not share deployment A.");
            Check(await Invoke(first, "FailClosed", prefix, algorithm) == 429, "Deployment A did not exhaust its quota.");
            Check(await Invoke(second, "FailClosed", "deployment-b:" + algorithm, algorithm) == 200,
                "Deployment A consumed deployment B's quota.");
        }
        Console.WriteLine("PASS: Live Redis isolates deployment prefixes for identical routes and clients");
        var clock = new ReviewReleaseChecks.ManualTime();
        var attempts = 0;
        await using (var replacement = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance, options, clock, async endpoint =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { throw new IOException("Injected construction failure"); }
            return await ConnectionMultiplexer.ConnectAsync(endpoint);
        }))
        {
            Check(!(await replacement.TryAcquireAsync("FixedWindow", "replacement", 10, 30, 0, null)).Available, "Construction failure was ignored.");
            clock.Advance(TimeSpan.FromSeconds(1));
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => replacement.TryAcquireAsync("FixedWindow", "replacement", 10, 30, 0, null).AsTask()));
            Check(attempts == 2 && results.All(r => r.Available) && results.Count(r => r.Allowed) == 10,
                "Concurrent replacement failed shared quota enforcement.");
            Check(!(await second.TryAcquireAsync("FixedWindow", "replacement", 10, 30, 0, null)).Allowed, "Replacement did not share Redis counters.");
        }
        Console.WriteLine("PASS: Live Redis recovers failed construction with one replacement and shared counters");
        await using (var control = await ConnectionMultiplexer.ConnectAsync(options.Value.ConnectionString + ",allowAdmin=true"))
        {
            await control.GetDatabase().ExecuteAsync("CLIENT", "PAUSE", 10000, "ALL");
            var interrupted = first.TryAcquireAsync("FixedWindow", "interrupted", 2, 30, 0, null).AsTask();
            Check(!interrupted.IsCompleted, "Lua operation did not wait during the server pause.");
            await server.StopAsync();
            Check(!(await interrupted.WaitAsync(TimeSpan.FromSeconds(5))).Available, "Interrupted Lua operation reported success.");
        }
        Console.WriteLine("PASS: Live Redis classifies an interrupted Lua operation as unavailable");
        await server.StopAsync();
        Check(await Invoke(first, "FailClosed") == 503, "Live outage bypassed FailClosed.");
        Check(await Invoke(second, "FailOpen") == 200, "Live outage did not follow FailOpen.");
        Console.WriteLine("PASS: Live Redis outage follows each route failure policy");
        await server.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var recovered = await first.TryAcquireAsync("FixedWindow", "recovery-probe", 1000, 30, 0, null, timeout.Token);
            if (recovered.Available)
            {
                break;
            }

            await Task.Delay(100, timeout.Token);
        }
        Check(await Invoke(first, "FailClosed") == 200, "Recovered Redis did not resume traffic.");
        while (!(await second.TryAcquireAsync("FixedWindow", "second-recovery", 1000, 30, 0, null, timeout.Token)).Available)
        {
            await Task.Delay(100, timeout.Token);
        }
        Check(await Invoke(second, "FailClosed") == 200 && await Invoke(first, "FailClosed") == 429,
            "Recovered replicas did not enforce their shared quota.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await first.TryAcquireAsync("FixedWindow", "canceled", 2, 30, 0, null, canceled.Token);
            throw new Exception("Canceled acquisition was not canceled.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: Live Redis reconnects after restart and preserves cancellation");
    }

    private static async Task<int> Invoke(IDistributedRateLimitStore store, string failureMode, string keyPrefix = "wafish:rl", string algorithm = "FixedWindow")
    {
        var route = new RouteConfig { RouteId = "redis-test", Match = new() { Path = "/redis-test" } };
        var snapshots = new GatewaySnapshotPublisher();
        snapshots.Publish(GatewaySnapshot.Empty with
        {
            Version = 1, Routes = [route],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(route.RouteId,
                [new("redis-binding", "rate-limit-redis", PluginBindingScope.Route, route.RouteId,
                    JsonSerializer.Serialize(new { enabled = true, failureMode, keyPrefix, algorithm, limit = 2, windowSeconds = 30 }), 1, 1, 0)])
        });
        var plans = new GatewayPluginExecutionPlanProvider(snapshots, NullLogger<GatewayPluginExecutionPlanProvider>.Instance);
        var middleware = new RedisRateLimitMiddleware(ctx => { ctx.Items["forwarded"] = true; return Task.CompletedTask; },
            NullLogger<RedisRateLimitMiddleware>.Instance, Options.Create(new GatewayMiddlewareOptions()), null!, store, plans);
        var context = new DefaultHttpContext();
        context.Request.Path = "/redis-test";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(route, null!, HttpTransformer.Default) });
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await middleware.InvokeAsync(context);
        Check(context.Items.ContainsKey("forwarded") == (context.Response.StatusCode == 200), "Failure response reached upstream.");
        return context.Response.StatusCode;
    }

    private sealed class StubStore : IDistributedRateLimitStore
    {
        public DistributedRateLimitResult Result { get; set; }
        public bool Throw { get; set; }
        public ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit,
            int windowSeconds, int burstBalance, string? redisConnectionString, CancellationToken cancellationToken = default)
            => Throw ? throw new IOException("Injected outage") : ValueTask.FromResult(Result);
    }

    private sealed class RedisServer : IAsyncDisposable
    {
        private readonly string _executable;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wafish-redis-test-" + Guid.NewGuid().ToString("N"));
        private Process? _process;
        public int Port { get; }
        public RedisServer(string executable)
        {
            _executable = executable;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Directory.CreateDirectory(_directory);
        }
        public async Task StartAsync()
        {
            var start = new ProcessStartInfo(_executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "--bind", "127.0.0.1", "--protected-mode", "yes", "--port", Port.ToString(), "--save", "", "--appendonly", "no", "--dir", _directory, "--logfile", Path.Combine(_directory, "redis.log") })
            {
                start.ArgumentList.Add(value);
            }

            _process = Process.Start(start) ?? throw new Exception("Redis failed to start.");
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (_process.HasExited)
                {
                    throw new Exception("Redis exited during startup: " + File.ReadAllText(Path.Combine(_directory, "redis.log")));
                }

                using var socket = new TcpClient();
                try { await socket.ConnectAsync(IPAddress.Loopback, Port, timeout.Token); return; }
                catch (SocketException) { await Task.Delay(20, timeout.Token); }
            }
        }
        public async Task StopAsync()
        {
            if (_process == null)
            {
                return;
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync();
            _process.Dispose(); _process = null;
        }
        public async ValueTask DisposeAsync() { await StopAsync(); Directory.Delete(_directory, true); }
    }
    private static void Check(bool condition, string message) { if (!condition)
        {
            throw new Exception(message);
        }
    }
}
