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
public sealed class RedisLuaRateLimitStore : IDistributedRateLimitStore, IDualWindowRateLimitStore, IAsyncDisposable
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
        => TryAcquireCoreAsync(algorithm, key, limit, windowSeconds, burstBalance, redisConnectionString, cancellationToken);

    public ValueTask<DistributedRateLimitResult> TryAcquireDualWindowAsync(string key, long limit, int windowSeconds,
        long burstLimit, int burstWindowSeconds, string? redisConnectionString, CancellationToken cancellationToken = default)
        => TryAcquireCoreAsync("DualWindow", key, limit, windowSeconds, 0, redisConnectionString, cancellationToken, burstLimit, burstWindowSeconds);

    private async ValueTask<DistributedRateLimitResult> TryAcquireCoreAsync(string algorithm, string key, long limit,
        int windowSeconds, int burstBalance, string? redisConnectionString, CancellationToken cancellationToken,
        long burstLimit = 0, int burstWindowSeconds = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dual = string.Equals(algorithm, "DualWindow", StringComparison.OrdinalIgnoreCase);
        if (limit <= 0 || windowSeconds <= 0 || (dual &&
            (limit > 1000000 || windowSeconds > 86400 || burstLimit is < 1 or > 1000000 || burstLimit > limit ||
             burstWindowSeconds < 1 || burstWindowSeconds >= windowSeconds)))
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
                "DUALWINDOW" => [limit, windowSeconds * 1000L, burstLimit, burstWindowSeconds * 1000L],
                "FIXEDWINDOW" => [limit, windowSeconds],
                "SLIDINGWINDOW" => [limit, windowSeconds * 1000L, Guid.NewGuid().ToString("N")],
                "TOKENBUCKET" => [limit, windowSeconds, Math.Max(0, burstBalance)],
                _ => throw new ArgumentException("Unsupported Redis rate-limit algorithm.")
            };
            var policy = FormattableString.Invariant($"{name}:{limit}:{windowSeconds}:{Math.Max(0, burstBalance)}");
            if (dual) policy = FormattableString.Invariant($"{name}:{limit}:{windowSeconds}:{burstLimit}:{burstWindowSeconds}");
            key += (dual ? ":v3-dual:" : ":v2:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy)));
            var script = name switch { "DUALWINDOW" => DualWindowScript, "SLIDINGWINDOW" => SlidingWindowScript, "TOKENBUCKET" => TokenBucketScript, _ => FixedWindowScript };
            var reply = (RedisResult[]?)await multiplexer.GetDatabase().ScriptEvaluateAsync(script, [(RedisKey)key], values).WaitAsync(cancellationToken);
            if (reply == null || reply.Length != (dual ? 4 : 3))
            {
                return DistributedRateLimitResult.Unavailable(limit);
            }

            var allowed = (long)reply[0] == 1;
            if (dual)
                return new(allowed, (long)reply[1], allowed ? Math.Max(0, (long)reply[2]) : 0,
                    allowed ? 0 : Math.Max(1, (long)reply[3]));
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

    // One hash key places both windows in one Redis Cluster slot. Redis TIME owns both boundaries.
    // ARGV: sustained limit/ms, burst limit/ms. Returns { allowed, selected limit, remaining, retry seconds }.
    private const string DualWindowScript = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local sustainedLimit = tonumber(ARGV[1])
        local sustainedWindow = tonumber(ARGV[2])
        local burstLimit = tonumber(ARGV[3])
        local burstWindow = tonumber(ARGV[4])
        local state = redis.call('HMGET', KEYS[1], 'sustainedCount', 'sustainedReset', 'burstCount', 'burstReset')
        local sustainedCount = tonumber(state[1]) or 0
        local sustainedReset = tonumber(state[2]) or 0
        local burstCount = tonumber(state[3]) or 0
        local burstReset = tonumber(state[4]) or 0
        if now >= sustainedReset then sustainedCount = 0; sustainedReset = now + sustainedWindow end
        if now >= burstReset then burstCount = 0; burstReset = now + burstWindow end
        local retry = 0
        local selectedLimit = burstLimit
        if burstCount >= burstLimit then retry = burstReset - now end
        if sustainedCount >= sustainedLimit and sustainedReset - now >= retry then
            retry = sustainedReset - now
            selectedLimit = sustainedLimit
        end
        -- Decide before any mutation. Denied requests neither charge nor extend either window.
        if retry > 0 then return {0, selectedLimit, 0, math.max(1, math.ceil(retry / 1000))} end
        sustainedCount = sustainedCount + 1
        burstCount = burstCount + 1
        redis.call('HSET', KEYS[1], 'sustainedCount', sustainedCount, 'sustainedReset', sustainedReset,
            'burstCount', burstCount, 'burstReset', burstReset)
        redis.call('PEXPIRE', KEYS[1], math.max(sustainedReset, burstReset) - now)
        local remaining = burstLimit - burstCount
        if sustainedLimit - sustainedCount < remaining then
            remaining = sustainedLimit - sustainedCount
            selectedLimit = sustainedLimit
        end
        return {1, selectedLimit, remaining, 0}
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
