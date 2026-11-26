using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wafish.Infrastructure;
using Wafish.Infrastructure.Middleware;
using Wafish.Infrastructure.State;
using Wafish.Models;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Plugins;
using Yarp.ReverseProxy.Model;

namespace Wafish.Plugin.RateLimit.Redis;

/// <summary>
/// Route-level distributed rate limiting middleware. Reads the enabled rate-limit-redis binding
/// from the current gateway snapshot, consults the shared <see cref="IDistributedRateLimitStore"/>
/// and rejects overflowing requests with 429 + Retry-After. Standard rate-limit response headers
/// (X-RateLimit-Limit / X-RateLimit-Remaining) are always attached. When Redis is unreachable the
/// route selects FailClosed (default) or FailOpen.
/// </summary>
public sealed class RedisRateLimitMiddleware : GatewayMiddlewareBase
{
    private readonly ILogger<RedisRateLimitMiddleware> _logger;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;
    private readonly IDistributedRateLimitStore _store;

    public RedisRateLimitMiddleware(
        RequestDelegate next,
        ILogger<RedisRateLimitMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions,
        IGatewayPluginManager pluginManager,
        IDistributedRateLimitStore store,
        GatewayPluginExecutionPlanProvider executionPlans,
        IDynamicYarpConfigService? yarpConfig = null)
        : base(next, dashOptions, pluginManager, yarpConfig)
    {
        _logger = logger;
        _store = store;
        _executionPlans = executionPlans;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;
        if (string.IsNullOrWhiteSpace(routeId) ||
            !_executionPlans.Current.RedisRateLimitByRoute.TryGetValue(routeId, out var config) ||
            !config.Enabled)
        {
            await Next(context);
            return;
        }

        string partition;
        if (config.PartitionKey == "Tenant")
        {
            var authenticated = context.User.Identities.Where(identity => identity.IsAuthenticated).ToArray();
            var tenants = authenticated.SelectMany(identity => identity.Claims)
                .Where(claim => claim.Type == config.TenantClaimType && claim.Issuer == config.TenantClaimIssuer)
                .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (string.IsNullOrWhiteSpace(config.TenantClaimType) || string.IsNullOrWhiteSpace(config.TenantClaimIssuer) ||
                config.AllowedTenants is null || config.AllowedTenants.Length is < 1 or > 256 || tenants.Length != 1 ||
                tenants[0].Length is < 1 or > 128 || !config.AllowedTenants.Contains(tenants[0], StringComparer.Ordinal))
            {
                context.Response.StatusCode = authenticated.Length == 0 ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
                context.Response.Headers["X-RateLimit-Status"] = "tenant-rejected";
                await context.Response.WriteAsJsonAsync(new { error = "Trusted tenant is required" }, context.RequestAborted);
                return;
            }
            partition = "tenant:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                config.TenantClaimIssuer + "\0" + config.TenantClaimType + "\0" + tenants[0])));
        }
        else if (config.PartitionKey == "IpAddress") partition = ClientIpResolver.GetClientIp(context) ?? "unknown";
        else
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var prefix = string.IsNullOrWhiteSpace(config.KeyPrefix) ? "wafish:rl" : config.KeyPrefix;
        var key = $"{prefix}:{routeId}:{partition}";

        DistributedRateLimitResult result;
        try
        {
            result = string.Equals(config.Algorithm, "DualWindow", StringComparison.OrdinalIgnoreCase)
                ? _store is ICombinedDistributedRateLimitStore combined
                    ? await combined.TryAcquireCombinedAsync(key, config.Limit, config.WindowSeconds, config.BurstLimit,
                        config.RedisConnectionString, context.RequestAborted)
                    : DistributedRateLimitResult.Unavailable(config.Limit)
                : await _store.TryAcquireAsync(config.Algorithm, key, config.Limit, config.WindowSeconds,
                    config.BurstBalance, config.RedisConnectionString, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Rate-limit store failed ({FailureType})", ex.GetType().Name);
            result = DistributedRateLimitResult.Unavailable(config.Limit);
        }
        if (!result.Available)
        {
            context.Response.Headers["X-RateLimit-Status"] = "unavailable";
            if (string.Equals(config.FailureMode, "FailOpen", StringComparison.OrdinalIgnoreCase))
            {
                await Next(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            await context.Response.WriteAsJsonAsync(new { error = "Rate limit unavailable", message = "The shared rate-limit store is unavailable. Retry later." }, context.RequestAborted);
            return;
        }

        context.Response.Headers["X-RateLimit-Limit"] = result.Limit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = result.Remaining.ToString();

        if (!result.Allowed)
        {
            _logger.LogWarning(
                "Distributed rate limit exceeded for {RateLimitKey} (algorithm={Algorithm}, limit={Limit}, window={WindowSeconds}s, retryAfter={RetryAfter}s)",
                key, config.Algorithm, config.Limit, config.WindowSeconds, result.RetryAfterSeconds);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";
            context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();

            await context.Response.WriteAsJsonAsync(new
            {
                error = "Too Many Requests",
                message = $"Distributed rate limit exceeded. Try again in {result.RetryAfterSeconds}s.",
                retryAfter = result.RetryAfterSeconds
            }, cancellationToken: context.RequestAborted);
            return;
        }

        await Next(context);
    }
}
