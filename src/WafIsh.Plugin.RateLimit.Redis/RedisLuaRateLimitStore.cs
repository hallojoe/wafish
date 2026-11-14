using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace WafIsh.Plugin.RateLimit.Redis;

public sealed class RedisRateLimitStoreOptions
{
    public string? ConnectionString { get; set; }
}

/// <summary>Atomic Redis counters. Availability is separate from the route's failure policy.</summary>
public sealed class RedisLuaRateLimitStore(ILogger<RedisLuaRateLimitStore> logger,
    IOptions<RedisRateLimitStoreOptions>? options = null) : IDistributedRateLimitStore, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Lazy<Task<ConnectionMultiplexer>>> _connections = new(StringComparer.Ordinal);
    private bool _disposed;
    private long _lastFailureLog;

    public async ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit,
        int windowSeconds, int burstBalance, string? redisConnectionString, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || windowSeconds <= 0) return DistributedRateLimitResult.Unavailable(limit);
        var connectionString = string.IsNullOrWhiteSpace(redisConnectionString) ? options?.Value.ConnectionString : redisConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) return DistributedRateLimitResult.Unavailable(limit);
        try
        {
            Lazy<Task<ConnectionMultiplexer>> connection;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_connections.TryGetValue(connectionString, out connection!))
                {
                    if (_connections.Count >= 32) return DistributedRateLimitResult.Unavailable(limit);
                    connection = new(() => ConnectAsync(connectionString));
                    _connections.Add(connectionString, connection);
                }
            }
            var multiplexer = await connection.Value.WaitAsync(cancellationToken);
            var name = algorithm.ToUpperInvariant();
            RedisValue[] values = name switch
            {
                "FIXEDWINDOW" => [limit, windowSeconds],
                "SLIDINGWINDOW" => [limit, windowSeconds * 1000L, Guid.NewGuid().ToString("N")],
                "TOKENBUCKET" => [limit, windowSeconds, Math.Max(0, burstBalance)],
                _ => throw new ArgumentException("Unsupported Redis rate-limit algorithm.")
            };
            var script = name switch { "SLIDINGWINDOW" => SlidingWindowScript, "TOKENBUCKET" => TokenBucketScript, _ => FixedWindowScript };
            var reply = (RedisResult[]?)await multiplexer.GetDatabase().ScriptEvaluateAsync(script, [(RedisKey)key], values).WaitAsync(cancellationToken);
            if (reply is not { Length: 3 }) return DistributedRateLimitResult.Unavailable(limit);
            var allowed = (long)reply[0] == 1;
            var capacity = name == "TOKENBUCKET" ? limit + Math.Max(0, burstBalance) : limit;
            return new(allowed, capacity, allowed ? Math.Max(0, (long)reply[1]) : 0,
                allowed ? 0 : Math.Max(1, (long)reply[2]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastFailureLog);
            if (now - previous >= 30000 && Interlocked.CompareExchange(ref _lastFailureLog, now, previous) == previous)
                logger.LogWarning("Redis rate-limit store unavailable ({FailureType}); the route failure policy applies.", ex.GetType().Name);
            return DistributedRateLimitResult.Unavailable(limit);
        }
    }

    private static Task<ConnectionMultiplexer> ConnectAsync(string connectionString)
    {
        var config = ConfigurationOptions.Parse(connectionString);
        config.AbortOnConnectFail = false;
        config.ConnectRetry = 0;
        config.ConnectTimeout = 1000;
        config.AsyncTimeout = 1000;
        config.BacklogPolicy = BacklogPolicy.FailFast;
        return ConnectionMultiplexer.ConnectAsync(config);
    }

    public async ValueTask DisposeAsync()
    {
        Lazy<Task<ConnectionMultiplexer>>[] connections;
        lock (_sync) { _disposed = true; connections = _connections.Values.ToArray(); _connections.Clear(); }
        foreach (var connection in connections.Where(c => c.IsValueCreated))
        {
            try { await (await connection.Value).DisposeAsync(); }
            catch (Exception) { /* A failed connection owns no usable multiplexer. */ }
        }
    }

    // ARGV: [1]=limit, [2]=windowSeconds. Returns { allowed, remaining, retryAfterSeconds }.
    private const string FixedWindowScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[2])
        end
        local limit = tonumber(ARGV[1])
        local ttl = redis.call('TTL', KEYS[1])
        if ttl < 1 then ttl = tonumber(ARGV[2]) end
        if current <= limit then
            return {1, limit - current, 0}
        end
        return {0, 0, ttl}
        """;

    // ARGV: [1]=limit, [2]=windowMilliseconds. Returns { allowed, remaining, retryAfterSeconds }.
    private const string SlidingWindowScript = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local window = tonumber(ARGV[2])
        local limit = tonumber(ARGV[1])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window)
        local count = redis.call('ZCARD', KEYS[1])
        if count < limit then
            redis.call('ZADD', KEYS[1], now, now .. '-' .. ARGV[3])
            redis.call('PEXPIRE', KEYS[1], window * 2)
            return {1, limit - count - 1, 0}
        end
        local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        local retry = window
        if oldest[2] then
            retry = math.max(1, tonumber(oldest[2]) + window - now)
        end
        return {0, 0, math.ceil(retry / 1000)}
        """;

    // ARGV: [1]=limit, [2]=windowSeconds, [3]=burstBalance. Returns { allowed, remaining, retryAfterSeconds }.
    private const string TokenBucketScript = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) + tonumber(time[2]) / 1000000
        local capacity = tonumber(ARGV[1]) + tonumber(ARGV[3])
        local rate = tonumber(ARGV[1]) / math.max(1, tonumber(ARGV[2]))
        local state = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
        local tokens = tonumber(state[1])
        local stamp = tonumber(state[2])
        if tokens == nil then tokens = capacity end
        if stamp == nil then stamp = now end
        tokens = math.min(capacity, tokens + math.max(0, now - stamp) * rate)
        local allowed = 0
        if tokens >= 1 then
            tokens = tokens - 1
            allowed = 1
        end
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', now)
        redis.call('EXPIRE', KEYS[1], math.max(1, math.ceil(capacity / rate)) * 2 + 1)
        local retry = 0
        if allowed == 0 and rate > 0 then
            retry = math.max(1, math.ceil((1 - tokens) / rate))
        end
        return {allowed, math.floor(tokens), retry}
        """;

}
