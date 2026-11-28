using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wafish.Models;

/// <summary>Canonical policy identity for process-local, named quota groups.</summary>
public static class LocalQuotaGroupPolicy
{
    public static RateLimitExecutionConfig Compile(RateLimitExecutionConfig config)
    {
        if (config.QuotaGroup is null) return config with { QuotaGroupPolicy = null };
        if (config.QuotaGroup.Length is < 1 or > 64 || !Regex.IsMatch(config.QuotaGroup, @"^[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Quota group names require 1–64 lowercase letters, digits, or hyphens, starting with a letter.");
        var partition = config.PartitionKey?.ToLowerInvariant();
        if (partition is not ("ipaddress" or "userid" or "route" or "global") || !Enum.IsDefined(config.Algorithm) || config.QueueLimit is < 0 or > 10000)
            throw new ArgumentException("Invalid named quota partition, algorithm, or queue capacity.");
        var settings = config.Algorithm + ":" + partition + ":" + config.QueueLimit.ToString(CultureInfo.InvariantCulture);
        if (config.Algorithm == RateLimitAlgorithm.TokenBucket)
        {
            if (config.TokenLimit is < 1 or > 1000000 || config.TokensPerPeriod is < 1 or > 1000000)
                throw new ArgumentException("Named token quotas require limits from 1 to 1000000.");
            var period = Duration(config.ReplenishmentPeriod);
            settings += FormattableString.Invariant($":{config.TokenLimit}:{config.TokensPerPeriod}:{period.Ticks}");
            config = config with { ReplenishmentPeriod = period.ToString("c", CultureInfo.InvariantCulture) };
        }
        else
        {
            if (config.PermitLimit is < 1 or > 1000000) throw new ArgumentException("Named quotas require 1–1000000 permits.");
            settings += ":" + config.PermitLimit.ToString(CultureInfo.InvariantCulture);
            if (config.Algorithm != RateLimitAlgorithm.Concurrency)
            {
                var window = Duration(config.Window);
                settings += ":" + window.Ticks.ToString(CultureInfo.InvariantCulture);
                config = config with { Window = window.ToString("c", CultureInfo.InvariantCulture) };
                if (config.Algorithm == RateLimitAlgorithm.SlidingWindow)
                {
                    if (config.SegmentsPerWindow is < 2 or > 100) throw new ArgumentException("Named sliding quotas require 2–100 segments.");
                    settings += ":" + config.SegmentsPerWindow.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        return config with { QuotaGroupPolicy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings))) };
    }

    private static TimeSpan Duration(string value)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Named quota durations are required.");
            var span = value.AsSpan().Trim();
            if (span.Length < 2 || !double.TryParse(span[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                throw new ArgumentException("Invalid named quota duration.");
            var seconds = amount * (char.ToLowerInvariant(span[^1]) switch { 's' => 1d, 'm' => 60d, 'h' => 3600d, 'd' => 86400d, _ => double.NaN });
            if (!double.IsFinite(seconds) || seconds is < .001 or > 86400) throw new ArgumentException("Named quota durations must be 1 ms–1 day.");
            result = TimeSpan.FromSeconds(seconds);
        }
        if (result < TimeSpan.FromMilliseconds(1) || result > TimeSpan.FromDays(1)) throw new ArgumentException("Named quota durations must be 1 ms–1 day.");
        return result;
    }
}
