using System.Threading.RateLimiting;

namespace Wafish.Infrastructure.State;

/// <summary>Optional support for shared group policies. Existing custom stores remain source-compatible.</summary>
public interface IGroupedRateLimiterStore
{
    /// <summary>Read-only compatibility check. Must not create or remove counters.</summary>
    bool IsGroupPolicyCompatible(string group, string policy);
    /// <summary>Atomically acquire a partition under the retained group policy.</summary>
    ValueTask<RateLimitLease?> AcquireGroupedAsync(string group, string policy, string partition,
        Func<RateLimiter> factory, CancellationToken cancellationToken = default);
}

/// <summary>An older request or retained counter owns a different policy for this group.</summary>
public sealed class RateLimitGroupPolicyConflictException(string group)
    : InvalidOperationException($"Quota group '{group}' still owns another policy.");
