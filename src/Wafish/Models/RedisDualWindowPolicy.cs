namespace Wafish.Models;

/// <summary>Bounds the opt-in burst and sustained fixed-window policy.</summary>
public static class RedisDualWindowPolicy
{
    public static RedisRateLimitExecutionConfig Compile(RedisRateLimitExecutionConfig config)
    {
        if (!string.Equals(config.Algorithm, "DualWindow", StringComparison.OrdinalIgnoreCase))
        {
            if (config.BurstLimit != null || config.BurstWindowSeconds != null)
                throw new ArgumentException("Burst limits and burst windows require the DualWindow algorithm.");
            return config;
        }
        if (config.BurstLimit is not (>= 1 and <= 1000000) || config.Limit is < 1 or > 1000000 ||
            config.BurstLimit > config.Limit || config.BurstWindowSeconds is not (>= 1 and <= 86400) ||
            config.WindowSeconds is < 1 or > 86400 || config.BurstWindowSeconds >= config.WindowSeconds || config.BurstBalance != 0)
            throw new ArgumentException("DualWindow requires limits of 1–1000000 and windows of 1–86400 seconds. The burst window must be shorter, its limit cannot exceed the sustained limit, and burstBalance must be zero.");
        return config;
    }
}
