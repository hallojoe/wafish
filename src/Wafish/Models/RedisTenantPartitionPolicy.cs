using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wafish.Models;

/// <summary>Explicit trust and provisioning for route-scoped tenant quotas.</summary>
public sealed record RedisTenantPartitionOptions
{
    public string ClaimType { get; init; } = string.Empty;
    public string ClaimIssuer { get; init; } = string.Empty;
    public string AuthenticationType { get; init; } = string.Empty;
    /// <summary>One to 1024 exact, case-sensitive tenant identifiers. Unknown tenants are rejected.</summary>
    public string[] AllowedTenants { get; init; } = [];
}

/// <summary>Immutable, bounded identity map compiled before request processing.</summary>
public sealed class RedisTenantPartitionPolicy
{
    private RedisTenantPartitionPolicy(RedisTenantPartitionOptions options, string routeId)
    {
        ClaimType = options.ClaimType; ClaimIssuer = options.ClaimIssuer; AuthenticationType = options.AuthenticationType;
        TenantKeys = options.AllowedTenants.ToFrozenDictionary(value => value, value => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { routeId, AuthenticationType, ClaimIssuer, ClaimType, value }))), StringComparer.Ordinal);
    }
    public string ClaimType { get; }
    public string ClaimIssuer { get; }
    public string AuthenticationType { get; }
    /// <summary>Tenant IDs mapped to opaque route-and-trust-bound quota suffixes.</summary>
    public FrozenDictionary<string, string> TenantKeys { get; }

    public static RedisRateLimitExecutionConfig Compile(RedisRateLimitExecutionConfig config, string routeId)
    {
        if (config.TenantPartition is not { } options) return config with { CompiledTenantPartition = null };
        if (config.Limit is < 1 or > 1000000 || config.WindowSeconds is < 1 or > 86400 || config.BurstBalance is < 0 or > 1000000)
            throw new ArgumentException("Tenant quotas require limits of 1–1000000, windows of 1–86400 seconds, and burst balance of 0–1000000.");
        if (config.BurstBalance > config.Limit || (config.BurstBalance != 0 && !string.Equals(config.Algorithm, "TokenBucket", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Tenant burst balance requires TokenBucket and cannot exceed its limit.");
        if (!Valid(options.ClaimType, 256) || !Valid(options.ClaimIssuer, 512) || !Valid(options.AuthenticationType, 128))
            throw new ArgumentException("Tenant quotas require bounded claim type, issuer, and authentication type values without surrounding whitespace or control characters.");
        if (options.AllowedTenants is not { Length: >= 1 and <= 1024 }
            || options.AllowedTenants.Any(value => !Valid(value, 128))
            || options.AllowedTenants.Distinct(StringComparer.Ordinal).Count() != options.AllowedTenants.Length)
            throw new ArgumentException("Tenant quotas require 1–1024 distinct tenant identifiers of 1–128 characters without surrounding whitespace or control characters.");
        return config with { TenantPartition = options with { AllowedTenants = [.. options.AllowedTenants] },
            CompiledTenantPartition = new(options, routeId) };
    }
    private static bool Valid(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && value.Trim() == value && !value.Any(char.IsControl);
}
