namespace Wafish.Models;

/// <summary>Validates trusted route costs without changing the underlying quota identity.</summary>
public static class LocalRequestCostPolicy
{
    public static RateLimitExecutionConfig Compile(RateLimitExecutionConfig config)
    {
        if (config.RequestCost is < 1 or > 1000000)
            throw new ArgumentException("Request cost must be an integer from 1 to 1000000.");
        if (config.RequestCost == 1) return config;
        var capacity = config.Algorithm switch
        {
            RateLimitAlgorithm.FixedWindow or RateLimitAlgorithm.SlidingWindow => config.PermitLimit,
            RateLimitAlgorithm.TokenBucket => config.TokenLimit,
            _ => throw new ArgumentException("Weighted request costs require FixedWindow, SlidingWindow, or TokenBucket.")
        };
        if (config.RequestCost > capacity)
            throw new ArgumentException("Request cost cannot exceed the configured permit or token capacity.");
        return config;
    }
}
