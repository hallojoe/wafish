using StackExchange.Redis;
using Wafish.Plugin.RateLimit.Redis;

internal static partial class RedisTenantChecks
{
    internal static async Task RunLiveAsync(RedisLuaRateLimitStore first, RedisLuaRateLimitStore second, string connectionString)
    {
        using var control = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var database = control.GetDatabase(); var server = control.GetServer(ConfigurationOptions.Parse(connectionString).EndPoints.Single());
        foreach (var algorithm in new[] { "FixedWindow", "SlidingWindow", "TokenBucket", "DualWindow" })
        {
            var prefix = "tenant-live-" + algorithm;
            var policy = Policy() with { Algorithm = algorithm, KeyPrefix = prefix, Limit = 3, RedisConnectionString = connectionString,
                BurstLimit = algorithm == "DualWindow" ? 2 : null, BurstWindowSeconds = algorithm == "DualWindow" ? 30 : null };
            var expected = algorithm == "DualWindow" ? 2 : 3;
            var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Invoke(i % 2 == 0 ? first : second, policy, User(), address: "192.0.2." + (i + 1))));
            Check(responses.Count(response => response.Response.StatusCode == 200) == expected
                && responses.All(response => response.Response.StatusCode is 200 or 429), "Live tenant quota did not share across clients: " + algorithm);
            Check((await Invoke(first, policy, User("beta"))).Response.StatusCode == 200, "Another tenant inherited exhausted quota: " + algorithm);
            var keys = server.Keys(pattern: prefix + ":*").ToArray();
            Check(keys.Length == 2, "Tenant requests created extra Redis keys: " + algorithm);
            foreach (var key in keys)
            {
                var ttl = await database.KeyTimeToLiveAsync(key);
                Check(ttl is { TotalSeconds: > 0 and <= 481 }, "Tenant key has unbounded expiry: " + algorithm);
                Check(!key.ToString().Contains("alpha") && !key.ToString().Contains("beta"), "Redis key exposed a raw tenant ID.");
            }
            var denied = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Invoke(first, policy with { FailureMode = "FailOpen" }, User("unknown-" + i))));
            Check(denied.All(response => response.Response.StatusCode == 403) && server.Keys(pattern: prefix + ":*").Count() == 2,
                "Unknown tenants allocated Redis keys: " + algorithm);
            var expanded = policy with { TenantPartition = Tenants() with { AllowedTenants = ["gamma", "beta", "alpha"] } };
            Check((await Invoke(second, expanded, User())).Response.StatusCode == 429, "Allowlist expansion reset an existing quota.");
            Check((await Invoke(first, policy, User(), routeId: "other-route")).Response.StatusCode == 200, "Routes shared tenant counters.");
            Check((await Invoke(first, policy with { KeyPrefix = prefix + "-other" }, User())).Response.StatusCode == 200, "Deployment prefixes shared tenant counters.");
            foreach (var key in keys) await database.KeyDeleteAsync(key);
            Check((await Invoke(second, policy, User())).Response.StatusCode == 200, "Expired tenant state did not restore allowance.");
        }
        var expiryPolicy = Policy() with { KeyPrefix = "tenant-expiry", Limit = 1, WindowSeconds = 1, RedisConnectionString = connectionString };
        Check((await Invoke(first, expiryPolicy, User())).Response.StatusCode == 200, "Tenant expiry setup failed.");
        var expiryKey = server.Keys(pattern: "tenant-expiry:*").Single();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (await database.KeyExistsAsync(expiryKey)) await Task.Delay(20, timeout.Token);
        Check((await Invoke(second, expiryPolicy, User())).Response.StatusCode == 200, "Natural expiry did not restore tenant allowance.");
        Console.WriteLine("PASS: Live Redis tenant quotas bound keys, share allowances across clients, isolate tenants and routes, and preserve expiry");
    }
}
