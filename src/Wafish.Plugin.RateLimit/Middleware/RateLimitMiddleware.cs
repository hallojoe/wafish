using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugins;
using Wafish.Infrastructure.State;
using Wafish.Services;
using Wafish.Models;
using Wafish.Infrastructure;
using System.Threading.RateLimiting;
using Yarp.ReverseProxy.Model;

namespace Wafish.Plugin.RateLimit;

/// <summary>
/// Route-level rate limiting middleware.
/// Reads the enabled rate-limit binding from the current gateway snapshot and enforces per-partition limits.
/// </summary>
public sealed class RateLimitMiddleware : GatewayMiddlewareBase
{
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;
    private readonly IRateLimiterStore _limiterStore;

    private const int MaxLimiterCount = 2000;
    private static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleLimiterThreshold = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time;
    private long _lastCleanupTicks;

    public RateLimitMiddleware(RequestDelegate next, ILogger<RateLimitMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions, IGatewayPluginManager pluginManager,
        IRateLimiterStore limiterStore, GatewayPluginExecutionPlanProvider executionPlans,
        IDynamicYarpConfigService? yarpConfig = null)
        : this(next, logger, dashOptions, pluginManager, limiterStore, executionPlans, TimeProvider.System, yarpConfig) { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public RateLimitMiddleware(
        RequestDelegate next,
        ILogger<RateLimitMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions,
        IGatewayPluginManager pluginManager,
        IRateLimiterStore limiterStore,
        GatewayPluginExecutionPlanProvider executionPlans,
        TimeProvider timeProvider,
        IDynamicYarpConfigService? yarpConfig = null)
        : base(next, dashOptions, pluginManager, yarpConfig)
    {
        _time = timeProvider;
        _lastCleanupTicks = _time.GetTimestamp();
        _logger = logger;
        _executionPlans = executionPlans;
        _limiterStore = limiterStore;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsDashboardRequest(context))
        {
            await Next(context);
            return;
        }

        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId
            ?? context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
        if (string.IsNullOrWhiteSpace(routeId) ||
            !TryGetBindingConfig(routeId, out var routeUid, out var config))
        {
            await Next(context);
            return;
        }

        var routeScopeId = routeUid;

        var partitionValue = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(GetPartitionValue(context, config.PartitionKey, config.QuotaGroup ?? (routeUid.Length > 0 ? routeUid : routeId)))));

        var configFingerprint = $"{config.Algorithm}:{config.PermitLimit}:{config.Window}:{config.QueueLimit}:{config.SegmentsPerWindow}:{config.TokenLimit}:{config.TokensPerPeriod}:{config.ReplenishmentPeriod}";
        var limiterKey = string.IsNullOrEmpty(routeScopeId)
            ? $"global:{partitionValue}:{configFingerprint}"
            : $"{routeScopeId}:{partitionValue}:{configFingerprint}";

        TryCleanup();
        RateLimitLease? acquired;
        try
        {
            if (config.QuotaGroup is { } group)
            {
                if (_limiterStore is not IGroupedRateLimiterStore grouped) throw new NotSupportedException("The store cannot enforce named quotas.");
                limiterKey = "group:" + group + ":" + partitionValue;
                acquired = config.RequestCost == 1
                    ? await grouped.AcquireGroupedAsync(group, config.QuotaGroupPolicy!, partitionValue, () => CreateLimiter(config), context.RequestAborted)
                    : _limiterStore is IWeightedGroupedRateLimiterStore weightedGroup
                        ? await weightedGroup.AcquireWeightedGroupedAsync(group, config.QuotaGroupPolicy!, partitionValue,
                            config.RequestCost, () => CreateLimiter(config), context.RequestAborted)
                        : throw new NotSupportedException("The store cannot enforce weighted named quotas.");
            }
            else acquired = config.RequestCost == 1
                ? await _limiterStore.AcquireAsync(limiterKey, () => CreateLimiter(config), context.RequestAborted)
                : _limiterStore is IWeightedRateLimiterStore weighted
                    ? await weighted.AcquireWeightedAsync(limiterKey, config.RequestCost, () => CreateLimiter(config), context.RequestAborted)
                    : throw new NotSupportedException("The store cannot enforce weighted quotas.");
        }
        catch (Exception error) when (error is RateLimitGroupPolicyConflictException or NotSupportedException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            await context.Response.WriteAsJsonAsync(new { error = config.QuotaGroup is null ? "Quota policy unavailable" : "Quota group policy unavailable" }, cancellationToken: context.RequestAborted);
            return;
        }
        using var lease = acquired;

        if (lease == null || !lease.IsAcquired)
        {
            _logger.LogWarning(
                "Rate limit exceeded for {LimiterKey} (algorithm={Algorithm}, limit={PermitLimit}, window={Window})",
                limiterKey, config.Algorithm, config.PermitLimit, config.Window);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";

            var retryAfter = GetRetryAfterSeconds(config, lease);
            if (retryAfter > 0)
            {
                context.Response.Headers.RetryAfter = retryAfter.ToString();
            }

            await context.Response.WriteAsJsonAsync(new
            {
                error = "Too Many Requests",
                message = $"Rate limit exceeded. Try again in {retryAfter}s.",
                retryAfter
            }, cancellationToken: context.RequestAborted);
            return;
        }

        await Next(context);
    }

    private bool TryGetBindingConfig(
        string routeId,
        out string routeUid,
        out RateLimitExecutionConfig config)
    {
        routeUid = string.Empty;
        config = null!;

        if (!_executionPlans.Current.RateLimitByRoute.TryGetValue(routeId, out var compiled) || !compiled.Enabled)
        {
            return false;
        }

        routeUid = compiled.RouteUid;
        config = compiled;
        return true;
    }

    private static RateLimiter CreateLimiter(RateLimitExecutionConfig config)
    {
        var window = ParseTimeSpan(config.Window);

        var queueLimit = Math.Max(0, config.QueueLimit);
        return config.Algorithm switch
        {
            RateLimitAlgorithm.SlidingWindow => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, config.PermitLimit),
                Window = window,
                SegmentsPerWindow = Math.Clamp(config.SegmentsPerWindow, 2, 100),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = queueLimit
            }),

            RateLimitAlgorithm.TokenBucket => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = Math.Max(1, config.TokenLimit),
                TokensPerPeriod = Math.Max(1, config.TokensPerPeriod),
                ReplenishmentPeriod = ParseTimeSpan(config.ReplenishmentPeriod),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = queueLimit,
                AutoReplenishment = true
            }),

            RateLimitAlgorithm.Concurrency => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = Math.Max(1, config.PermitLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = queueLimit
            }),

            _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, config.PermitLimit),
                Window = window,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = queueLimit
            })
        };
    }

    private void TryCleanup()
    {
        var lastTicks = Interlocked.Read(ref _lastCleanupTicks);
        if (_time.GetElapsedTime(lastTicks) < DefaultCleanupInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _lastCleanupTicks, _time.GetTimestamp());
        _limiterStore.Cleanup(StaleLimiterThreshold, MaxLimiterCount);
    }

    private static string GetPartitionValue(HttpContext context, string partitionKey, string routeId)
    {
        return partitionKey.ToLowerInvariant() switch
        {
            "ipaddress" => GetClientIp(context) ?? "unknown",
            "userid" => context.User?.Identity?.Name ?? "anonymous",
            "route" => routeId,
            "global" => "gateway-global",
            _ => GetClientIp(context) ?? "unknown"
        };
    }

    private static string? GetClientIp(HttpContext context)
    {
        return ClientIpResolver.GetClientIp(context);
    }

    private static TimeSpan ParseTimeSpan(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromMinutes(1);
        }

        if (TimeSpan.TryParse(value, out var ts))
        {
            return ts;
        }

        var span = value.AsSpan().Trim();
        if (span.Length < 2)
        {
            return TimeSpan.FromMinutes(1);
        }

        var numPart = span[..^1];
        var unit = char.ToLowerInvariant(span[^1]);

        if (!double.TryParse(numPart, out var num))
        {
            return TimeSpan.FromMinutes(1);
        }

        return unit switch
        {
            's' => TimeSpan.FromSeconds(num),
            'm' => TimeSpan.FromMinutes(num),
            'h' => TimeSpan.FromHours(num),
            'd' => TimeSpan.FromDays(num),
            _ => TimeSpan.FromMinutes(num)
        };
    }

    private static long GetRetryAfterSeconds(RateLimitExecutionConfig config, RateLimitLease? lease)
    {
        if (lease != null && lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            return Math.Max(1L, (long)Math.Ceiling(retryAfter.TotalSeconds));
        }
        var interval = config.Algorithm switch
        {
            RateLimitAlgorithm.TokenBucket => ParseTimeSpan(config.ReplenishmentPeriod),
            RateLimitAlgorithm.Concurrency => TimeSpan.FromSeconds(1),
            _ => ParseTimeSpan(config.Window)
        };
        return Math.Max(1L, (long)Math.Ceiling(interval.TotalSeconds));
    }

}
