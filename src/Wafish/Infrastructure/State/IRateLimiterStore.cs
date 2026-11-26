using System.Threading.RateLimiting;

namespace Wafish.Infrastructure.State;

/// <summary>
/// Manages in-memory rate limiter instances. Registered as a Singleton so that
/// rate limiter lifecycle (creation, cleanup, disposal) is controlled by DI.
/// </summary>
public interface IRateLimiterStore
{
    /// <summary>Reserve a partition until the returned lease is disposed. Null means partition capacity is exhausted.</summary>
    ValueTask<RateLimitLease?> AcquireAsync(string key, Func<RateLimiter> factory, CancellationToken cancellationToken = default);

    /// <summary>Current number of limiters.</summary>
    int Count { get; }

    /// <summary>
    /// Evict stale, fully idle entries only. Active and queued leases retain their partitions.
    /// The maxCount argument remains for compatibility; admission enforces the store capacity.
    /// </summary>
    void Cleanup(TimeSpan staleThreshold, int maxCount);
}

/// <summary>Optional atomic acquisition of a configured number of permits.</summary>
public interface IWeightedRateLimiterStore : IRateLimiterStore
{
    ValueTask<RateLimitLease?> AcquireWeightedAsync(string key, int permits, Func<RateLimiter> factory, CancellationToken cancellationToken = default);
}

/// <summary>Wrapper tracking last access time for stale-eviction cleanup strategy.</summary>
public sealed class RateLimiterEntry
{
    public RateLimiter Limiter { get; }
    public DateTime LastAccessedAt { get; set; }

    public RateLimiterEntry(RateLimiter limiter)
    {
        Limiter = limiter;
        LastAccessedAt = DateTime.Now;
    }
}
