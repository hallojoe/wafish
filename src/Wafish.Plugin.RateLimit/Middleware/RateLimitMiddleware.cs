using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugins;
using Wafish.Infrastructure.State;
using Wafish.Services;
using Wafish.Storage;
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

        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;
        if (string.IsNullOrWhiteSpace(routeId) ||
            !TryGetBindingConfig(routeId, out var routeUid, out var config))
        {
            await Next(context);
            return;
        }

        var routeKey = routeId;
        var routeScopeId = routeUid;

        var partitionValue = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(GetPartitionValue(context, config.PartitionKey, routeUid.Length > 0 ? routeUid : routeId))));

        var configFingerprint = $"{config.Algorithm}:{config.PermitLimit}:{config.Window}:{config.QueueLimit}:{config.SegmentsPerWindow}:{config.TokenLimit}:{config.TokensPerPeriod}:{config.ReplenishmentPeriod}";
        var limiterKey = string.IsNullOrEmpty(routeScopeId)
            ? $"global:{partitionValue}:{configFingerprint}"
            : $"{routeScopeId}:{partitionValue}:{configFingerprint}";

        TryCleanup();
        using var lease = await _limiterStore.AcquireAsync(limiterKey, () => CreateLimiter(limiterKey, config), context.RequestAborted);

        if (lease == null || !lease.IsAcquired)
        {
            var clientIp = GetClientIp(context) ?? "unknown";
            _logger.LogWarning(
                "Rate limit exceeded for {LimiterKey} (algorithm={Algorithm}, limit={PermitLimit}, window={Window})",
                limiterKey, config.Algorithm, config.PermitLimit, config.Window);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";

            var retryAfter = GetRetryAfterSeconds(config);
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
        out RouteRateLimitConfig config)
    {
        routeUid = string.Empty;
        config = null!;

        if (!_executionPlans.Current.RateLimitByRoute.TryGetValue(routeId, out var compiled) || !compiled.Enabled)
        {
            return false;
        }

        routeUid = compiled.RouteUid;
        config = new RouteRateLimitConfig
        {
            Enabled = true,
            Algorithm = compiled.Algorithm,
            PermitLimit = compiled.PermitLimit,
            Window = compiled.Window,
            QueueLimit = compiled.QueueLimit,
            PartitionKey = compiled.PartitionKey,
            SegmentsPerWindow = compiled.SegmentsPerWindow,
            TokenLimit = compiled.TokenLimit,
            TokensPerPeriod = compiled.TokensPerPeriod,
            ReplenishmentPeriod = compiled.ReplenishmentPeriod
        };
        return true;
    }

    private static RateLimiter CreateLimiter(string key, RouteRateLimitConfig config)
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

    private static int GetRetryAfterSeconds(RouteRateLimitConfig config)
    {
        var window = ParseTimeSpan(config.Window);
        return Math.Max(1, (int)Math.Ceiling(window.TotalSeconds));
    }

    private sealed class RouteRateLimitConfig
    {
        public bool Enabled { get; set; }
        public RateLimitAlgorithm Algorithm { get; set; } = RateLimitAlgorithm.FixedWindow;
        public int PermitLimit { get; set; } = 100;
        public string Window { get; set; } = "1m";
        public int QueueLimit { get; set; } = 0;
        public string PartitionKey { get; set; } = "IpAddress";
        public int SegmentsPerWindow { get; set; } = 4;
        public int TokenLimit { get; set; } = 100;
        public int TokensPerPeriod { get; set; } = 100;
        public string ReplenishmentPeriod { get; set; } = "1s";
    }
}
