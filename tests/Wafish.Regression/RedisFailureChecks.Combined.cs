using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugin.RateLimit.Redis;

internal static partial class RedisFailureChecks
{
    private static async Task CombinedPolicyAsync()
    {
        var schema = new RedisRateLimitPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
        var validator = new PluginConfigurationSchemaValidator();
        foreach (var json in new[] { "{}", "{\"algorithm\":\"TokenBucket\",\"burstBalance\":2}",
            "{\"algorithm\":\"DualWindow\",\"limit\":100,\"windowSeconds\":60,\"burstLimit\":10}" })
            Check(validator.TryValidate(json, schema, out _, out _), "Valid quota configuration was rejected.");
        foreach (var json in new[] { "{\"algorithm\":\"DualWindow\"}",
            "{\"algorithm\":\"DualWindow\",\"windowSeconds\":1,\"burstLimit\":10}",
            "{\"algorithm\":\"DualWindow\",\"windowSeconds\":60,\"burstLimit\":0}",
            "{\"algorithm\":\"DualWindow\",\"windowSeconds\":604801,\"burstLimit\":10}",
            "{\"algorithm\":\"DualWindow\",\"limit\":1000001,\"windowSeconds\":60,\"burstLimit\":10}" })
            Check(!validator.TryValidate(json, schema, out _, out _), "Invalid dual-window policy was accepted.");
        var unsupported = new StubStore();
        Check(await Invoke(unsupported, "FailClosed", algorithm: "DualWindow") == 503, "Unsupported store bypassed FailClosed.");
        Check(await Invoke(unsupported, "FailOpen", algorithm: "DualWindow") == 200, "Unsupported store ignored explicit FailOpen.");
        var store = new CombinedStub();
        Check(await Invoke(store, "FailClosed", algorithm: "DualWindow") == 200 && store.Calls == 1, "Middleware did not use atomic acquisition.");
        await using var actual = new RedisLuaRateLimitStore(NullLogger<RedisLuaRateLimitStore>.Instance);
        foreach (var (limit, window, burst) in new[] { (0, 60, 1), (1, 1, 1), (1, 604801, 1), (1000001, 60, 1), (1, 60, 0), (1, 60, 1000001) })
            Check(!(await actual.TryAcquireCombinedAsync("invalid", limit, window, burst, null)).Available, "Store accepted invalid combined bounds.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await actual.TryAcquireCombinedAsync("cancel", 10, 60, 2, null, canceled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }

    private static async Task CombinedLiveAsync(RedisLuaRateLimitStore first, RedisLuaRateLimitStore second, string connection)
    {
        await using var control = await ConnectionMultiplexer.ConnectAsync(connection);
        var database = control.GetDatabase();
        var server = control.GetServer(control.GetEndPoints().Single());
        await first.TryAcquireAsync("FixedWindow", "combined-warm-first", 1, 30, 0, null);
        await second.TryAcquireAsync("FixedWindow", "combined-warm-second", 1, 30, 0, null);
        var key = "composite:" + Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => (i % 2 == 0 ? first : second)
            .TryAcquireCombinedAsync(key, 10, 30, 3, null).AsTask()));
        Check(results.All(r => r.Available) && results.Count(r => r.Allowed) == 3, "Concurrent burst allowance was not atomic.");
        var redisKey = server.Keys(pattern: key + ":*").Single();
        Check(await database.KeyTypeAsync(redisKey) == RedisType.Hash, "Combined quotas did not share one hash key.");
        Check((long)await database.HashGetAsync(redisKey, "sustainedCount") == 3, "Rejected bursts consumed sustained permits.");
        var ttl = await database.KeyTimeToLiveAsync(redisKey);
        Check(ttl > TimeSpan.Zero && ttl <= TimeSpan.FromSeconds(30), "Combined counter expiry is unbounded.");
        await database.HashSetAsync(redisKey, "burstStart", 0);
        var afterBurstReset = await second.TryAcquireCombinedAsync(key, 10, 30, 3, null);
        Check(afterBurstReset.Allowed && afterBurstReset.Remaining == 2 && afterBurstReset.Limit == 3, "Burst reset lost sustained state or headers.");
        Check((long)await database.HashGetAsync(redisKey, "sustainedCount") == 4, "Burst reset erased sustained usage.");
        await database.HashSetAsync(redisKey, [new("sustainedCount", 10), new("burstCount", 0)]);
        var before = await database.HashGetAllAsync(redisKey);
        var denied = await first.TryAcquireCombinedAsync(key, 10, 30, 3, null);
        var after = await database.HashGetAllAsync(redisKey);
        Check(!denied.Allowed && denied.Available && denied.RetryAfterSeconds is > 0 and <= 30, "Sustained exhaustion did not reject.");
        Check(before.OrderBy(e => e.Name.ToString()).SequenceEqual(after.OrderBy(e => e.Name.ToString())), "Rejected sustained request changed burst state.");
        await database.HashSetAsync(redisKey, "sustainedStart", 0);
        Check((await second.TryAcquireCombinedAsync(key, 10, 30, 3, null)).Allowed, "Expired sustained window did not recover.");
        Check((long)await database.HashGetAsync(redisKey, "sustainedCount") == 1, "Sustained reset retained old usage.");
        Check((await first.TryAcquireCombinedAsync(key, 11, 30, 3, null)).Allowed && server.Keys(pattern: key + ":*").Count() == 2,
            "Policy replacement reused old counters.");
        Check(await Invoke(first, "FailClosed", "composite-middleware", "DualWindow") == 200 &&
            await Invoke(second, "FailClosed", "composite-middleware", "DualWindow") == 200 &&
            await Invoke(first, "FailClosed", "composite-middleware", "DualWindow") == 429, "Middleware did not share combined quotas.");
        Console.WriteLine("PASS: Live Redis combined quotas enforce atomic burst/sustained decisions, reset, expiry, and replica sharing");
    }

    private sealed class CombinedStub : ICombinedDistributedRateLimitStore
    {
        public int Calls { get; private set; }
        public ValueTask<DistributedRateLimitResult> TryAcquireCombinedAsync(string key, long sustainedLimit, int sustainedWindowSeconds,
            int burstLimit, string? redisConnectionString, CancellationToken cancellationToken = default)
        {
            Check(sustainedLimit == 2 && sustainedWindowSeconds == 30 && burstLimit == 10, "Compiled quota settings changed.");
            Calls++;
            return ValueTask.FromResult(new DistributedRateLimitResult(true, 2, 1, 0));
        }
        public ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit,
            int windowSeconds, int burstBalance, string? redisConnectionString, CancellationToken cancellationToken = default)
            => throw new Exception("Combined quota was split into independent acquisitions.");
    }
}
