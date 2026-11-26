using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Wafish.Plugin.RateLimit.Redis;

public sealed class RedisRateLimitStoreOptions
{
    public string? ConnectionString { get; set; }
}

/// <summary>Atomic Redis counters. Availability is separate from the route's failure policy.</summary>
public sealed class RedisLuaRateLimitStore : IDistributedRateLimitStore, ICombinedDistributedRateLimitStore, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ConnectionEntry> _connections = new(StringComparer.Ordinal);
    private readonly ILogger<RedisLuaRateLimitStore> _logger;
    private readonly IOptions<RedisRateLimitStoreOptions>? _options;
    private readonly TimeProvider _time;
    private readonly Func<string, Task<IConnectionMultiplexer>> _connect;

    public RedisLuaRateLimitStore(ILogger<RedisLuaRateLimitStore> logger,
        IOptions<RedisRateLimitStoreOptions>? options = null)
        : this(logger, options, TimeProvider.System, ConnectAsync) { }

    internal RedisLuaRateLimitStore(ILogger<RedisLuaRateLimitStore> logger,
        IOptions<RedisRateLimitStoreOptions>? options, TimeProvider time,
        Func<string, Task<IConnectionMultiplexer>> connect)
    {
        _logger = logger;
        _options = options;
        _time = time;
        _connect = connect;
    }

    private sealed class ConnectionEntry
    {
        public Task<IConnectionMultiplexer>? Task { get; set; }
        public int Failures { get; set; }
        public long FailedAt { get; set; }
    }
    private bool _disposed;
    private long _lastFailureLog;

    public ValueTask<DistributedRateLimitResult> TryAcquireAsync(string algorithm, string key, long limit,
        int windowSeconds, int burstBalance, string? redisConnectionString, CancellationToken cancellationToken = default)
        => AcquireCoreAsync(algorithm, key, limit, windowSeconds, burstBalance, null, redisConnectionString, cancellationToken);

    public ValueTask<DistributedRateLimitResult> TryAcquireCombinedAsync(string key, long sustainedLimit,
        int sustainedWindowSeconds, int burstLimit, string? redisConnectionString, CancellationToken cancellationToken = default)
        => AcquireCoreAsync("DualWindow", key, sustainedLimit, sustainedWindowSeconds, 0, burstLimit, redisConnectionString, cancellationToken);

    private async ValueTask<DistributedRateLimitResult> AcquireCoreAsync(string algorithm, string key, long limit,
        int windowSeconds, int burstBalance, int? combinedBurstLimit, string? redisConnectionString, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || windowSeconds <= 0 || (combinedBurstLimit.HasValue &&
            (limit > 1000000 || windowSeconds is < 2 or > 604800 || combinedBurstLimit.Value is < 1 or > 1000000)))
        {
            return DistributedRateLimitResult.Unavailable(limit);
        }

        var connectionString = string.IsNullOrWhiteSpace(redisConnectionString) ? _options?.Value.ConnectionString : redisConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return DistributedRateLimitResult.Unavailable(limit);
        }

        try
        {
            Task<IConnectionMultiplexer> connection;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_connections.TryGetValue(connectionString, out var entry))
                {
                    if (_connections.Count >= 32)
                    {
                        return DistributedRateLimitResult.Unavailable(limit);
                    }

                    entry = new ConnectionEntry();
                    _connections.Add(connectionString, entry);
                }

                if (entry.Task == null ||
                    ((entry.Task.IsFaulted || entry.Task.IsCanceled) &&
                     _time.GetElapsedTime(entry.FailedAt) >= TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(5, entry.Failures - 1)))))
                {
                    // Start under the ownership lock so shutdown also awaits a pending connection.
                    entry.Task = ConnectOwnedAsync(connectionString, entry);
                }
                connection = entry.Task;
            }
            var multiplexer = await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
            var name = algorithm.ToUpperInvariant();
            RedisValue[] values = name switch
            {
                "DUALWINDOW" when combinedBurstLimit.HasValue => [limit, windowSeconds * 1000L, combinedBurstLimit.Value],
                "FIXEDWINDOW" => [limit, windowSeconds],
                "SLIDINGWINDOW" => [limit, windowSeconds * 1000L, Guid.NewGuid().ToString("N")],
                "TOKENBUCKET" => [limit, windowSeconds, Math.Max(0, burstBalance)],
                _ => throw new ArgumentException("Unsupported Redis rate-limit algorithm.")
            };
            var policy = FormattableString.Invariant($"{name}:{limit}:{windowSeconds}:{Math.Max(0, burstBalance)}");
            if (combinedBurstLimit.HasValue) policy += FormattableString.Invariant($":{combinedBurstLimit.Value}:1000");
            key += (combinedBurstLimit.HasValue ? ":v3:" : ":v2:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy)));
            var script = name switch { "DUALWINDOW" => DualWindowScript, "SLIDINGWINDOW" => SlidingWindowScript, "TOKENBUCKET" => TokenBucketScript, _ => FixedWindowScript };
            var reply = (RedisResult[]?)await multiplexer.GetDatabase().ScriptEvaluateAsync(script, [(RedisKey)key], values).WaitAsync(cancellationToken);
            if (reply is not { Length: 3 })
            {
                return DistributedRateLimitResult.Unavailable(limit);
            }

            var allowed = (long)reply[0] == 1;
            var capacity = combinedBurstLimit.HasValue ? Math.Min(limit, combinedBurstLimit.Value) : name == "TOKENBUCKET" ? limit + Math.Max(0, burstBalance) : limit;
            return new(allowed, capacity, allowed ? Math.Max(0, (long)reply[1]) : 0,
                allowed ? 0 : Math.Max(1, (long)reply[2]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastFailureLog);
            if (now - previous >= 30000 && Interlocked.CompareExchange(ref _lastFailureLog, now, previous) == previous)
            {
                _logger.LogWarning("Redis rate-limit store unavailable ({FailureType}); the route failure policy applies.", ex.GetType().Name);
            }

            return DistributedRateLimitResult.Unavailable(limit);
        }
    }

    private async Task<IConnectionMultiplexer> ConnectOwnedAsync(string connectionString, ConnectionEntry entry)
    {
        try
        {
            var connection = await _connect(connectionString).ConfigureAwait(false);
            lock (_sync) { entry.Failures = 0; }
            return connection;
        }
        catch
        {
            lock (_sync)
            {
                entry.Failures = Math.Min(6, entry.Failures + 1);
                entry.FailedAt = _time.GetTimestamp();
            }
            throw;
        }
    }

    private static async Task<IConnectionMultiplexer> ConnectAsync(string connectionString)
    {
        var config = ConfigurationOptions.Parse(connectionString);
        config.AbortOnConnectFail = false;
        config.ConnectRetry = 0;
        config.ConnectTimeout = 1000;
        config.AsyncTimeout = 1000;
        config.BacklogPolicy = BacklogPolicy.FailFast;
        return await ConnectionMultiplexer.ConnectAsync(config).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task<IConnectionMultiplexer>[] connections;
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            connections = _connections.Values.Select(c => c.Task!).ToArray();
            _connections.Clear();
        }
        foreach (var connection in connections)
        {
            try { await (await connection.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* A failed connection owns no usable multiplexer. */ }
        }
    }

    // One hash key keeps both windows in the same Redis Cluster slot. Denial performs no writes.
    // ARGV: sustained limit, sustained milliseconds, burst limit. Burst window: 1000 milliseconds.
    private const string DualWindowScript = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local limit = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local burstLimit = tonumber(ARGV[3])
        local state = redis.call('HMGET', KEYS[1], 'burstCount', 'burstStart', 'sustainedCount', 'sustainedStart')
        local burstCount = tonumber(state[1]) or 0
        local burstStart = tonumber(state[2]) or now
        local sustainedCount = tonumber(state[3]) or 0
        local sustainedStart = tonumber(state[4]) or now
        if now - burstStart >= 1000 then burstCount = 0; burstStart = now end
        if now - sustainedStart >= window then sustainedCount = 0; sustainedStart = now end
        local retry = 0
        if burstCount >= burstLimit then retry = math.max(retry, burstStart + 1000 - now) end
        if sustainedCount >= limit then retry = math.max(retry, sustainedStart + window - now) end
        if retry > 0 then return {0, 0, math.max(1, math.ceil(retry / 1000))} end
        burstCount = burstCount + 1
        sustainedCount = sustainedCount + 1
        redis.call('HSET', KEYS[1], 'burstCount', burstCount, 'burstStart', burstStart,
            'sustainedCount', sustainedCount, 'sustainedStart', sustainedStart)
        redis.call('PEXPIRE', KEYS[1], math.max(1, math.max(burstStart + 1000, sustainedStart + window) - now))
        return {1, math.min(burstLimit - burstCount, limit - sustainedCount), 0}
        """;

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
