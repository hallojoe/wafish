using System.Threading.RateLimiting;

namespace Wafish.Infrastructure.State;

/// <summary>Optional atomic acquisition of multiple permits. Existing store implementations remain supported for unit costs.</summary>
public interface IWeightedRateLimiterStore
{
    /// <summary>Acquire all permits or none. Dispose the returned lease to release partition ownership.</summary>
    ValueTask<RateLimitLease?> AcquireWeightedAsync(string key, int permitCount, Func<RateLimiter> factory,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional weighted acquisition from a retained named quota policy.</summary>
public interface IWeightedGroupedRateLimiterStore
{
    /// <summary>Acquire all permits or none under the retained group policy. The caller must dispose the lease.</summary>
    ValueTask<RateLimitLease?> AcquireWeightedGroupedAsync(string group, string policy, string partition, int permitCount,
        Func<RateLimiter> factory, CancellationToken cancellationToken = default);
}
