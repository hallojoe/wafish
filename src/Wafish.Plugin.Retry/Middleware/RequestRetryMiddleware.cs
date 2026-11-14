using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugins;
using Wafish.Infrastructure.State;
using Wafish.Infrastructure.Resilience;
using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using Wafish.Plugin.Retry;
using Wafish.Services;
using Microsoft.IO;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Health;

namespace Wafish.Plugin.Retry;

/// <summary>
/// Request retry middleware for failed proxy requests.
/// Retries 502/503/504 responses with exponential backoff + jitter.
/// Supports cross-destination retry and circuit-breaker awareness.
/// 
/// Replay requires a complete bounded request body. Response transmission ends retry eligibility.
/// </summary>
public sealed class RequestRetryMiddleware : GatewayMiddlewareBase
{
    private readonly ILogger<RequestRetryMiddleware> _logger;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;
    private readonly ICircuitStateStore _circuitStore;
    private readonly IDestinationCandidateCoordinator _destinationCoordinator;
    private readonly ClusterRetryBudget _fallbackBudget = new(Options.Create(new ClusterRetryBudgetOptions()));

    private static readonly HashSet<string> NonIdempotentMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PATCH", "PUT", "DELETE"
    };

    public RequestRetryMiddleware(
        RequestDelegate next,
        ILogger<RequestRetryMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions,
        IGatewayPluginManager pluginManager,
        RecyclableMemoryStreamManager memoryStreamManager,
        ICircuitStateStore circuitStore,
        IDestinationCandidateCoordinator destinationCoordinator,
        GatewayPluginExecutionPlanProvider executionPlans,
        IDynamicYarpConfigService? yarpConfig = null)
        : base(next, dashOptions, pluginManager, yarpConfig)
    {
        _logger = logger;
        _circuitStore = circuitStore;
        _destinationCoordinator = destinationCoordinator;
        _executionPlans = executionPlans;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsDashboardRequest(context))
        {
            await Next(context);
            return;
        }

        var proxyFeature = context.Features.Get<IReverseProxyFeature>();
        var routeConfig = proxyFeature?.Route?.Config;
        var clusterId = routeConfig?.ClusterId;

        if (routeConfig == null)
        {
            await Next(context);
            return;
        }

        if (!TryParseRetryConfig(routeConfig.RouteId, out var retryConfig))
        {
            await Next(context);
            return;
        }

        var clock = context.RequestServices?.GetService<TimeProvider>() ?? TimeProvider.System;
        var callerCancellation = context.RequestAborted;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(retryConfig.TotalTimeoutSeconds), clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, deadline.Token);
        context.RequestAborted = lifetime.Token;
        try
        {
            await ExecuteAsync(context, proxyFeature, routeConfig, retryConfig, clock).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
        {
            if (context.Response.HasStarted) { context.Abort(); }
            else { context.Response.Clear(); context.Response.StatusCode = StatusCodes.Status504GatewayTimeout; }
        }
        finally { context.RequestAborted = callerCancellation; }
    }

    private async Task ExecuteAsync(HttpContext context, IReverseProxyFeature? proxyFeature,
        RouteConfig routeConfig, ParsedRetryConfig retryConfig, TimeProvider clock)
    {
        if (!retryConfig.RetryNonIdempotent && NonIdempotentMethods.Contains(context.Request.Method))
        {
            await Next(context);
            return;
        }

        var originalRequestBody = context.Request.Body;
        var originalLength = context.Request.ContentLength;
        var originalPosition = originalRequestBody.CanSeek ? originalRequestBody.Position : (long?)null;
        var canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? (originalLength is > 0 || originalLength is null && originalRequestBody != Stream.Null);
        if (retryConfig.MaxRetries == 0 || context.Response.HasStarted ||
            canHaveBody && (originalLength is null || originalLength > MaxRetryBodySizeBytes))
        {
            await Next(context);
            return;
        }

        byte[]? requestBuffer = null;
        var attempt = 0;
        var originalAborted = context.RequestAborted;
        var originalFeature = context.Features.Get<IHttpResponseBodyFeature>()!;
        var originalHeaders = context.Response.Headers.ToArray();
        context.Response.OnStarting(() =>
        {
            if (attempt > 0)
            {
                context.Response.Headers["X-Retry-Count"] = attempt.ToString();
            }

            return Task.CompletedTask;
        });
        try
        {
            var length = (int)(originalLength ?? 0);
            if (length > 0)
            {
                requestBuffer = ArrayPool<byte>.Shared.Rent(length);
                using var preparationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(retryConfig.TimeoutSeconds), clock);
                using var preparationTimeout = CancellationTokenSource.CreateLinkedTokenSource(originalAborted, preparationDeadline.Token);
                var read = 0;
                while (read < length)
                {
                    var count = await originalRequestBody.ReadAsync(requestBuffer.AsMemory(read, length - read), preparationTimeout.Token);
                    if (count == 0)
                    {
                        throw new BadHttpRequestException("Incomplete request body", StatusCodes.Status400BadRequest);
                    }

                    read += count;
                }
            }

            while (true)
            {
                originalAborted.ThrowIfCancellationRequested();
                var candidates = await _destinationCoordinator.ApplyAsync(context,
                    attempt > 0 && retryConfig.UseDifferentDestination, originalAborted);
                if (candidates.Count == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                using var replay = requestBuffer is null ? null : new MemoryStream(requestBuffer, 0, length, writable: false);
                if (replay != null)
                {
                    context.Request.Body = replay;
                }

                using var response = new RetryResponseBody(context, originalFeature,
                    status => attempt < retryConfig.MaxRetries && retryConfig.StatusCodes.Contains(status));
                context.Features.Set<IHttpResponseBodyFeature>(response);
                using var attemptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(retryConfig.TimeoutSeconds), clock);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(originalAborted, attemptDeadline.Token);
                context.RequestAborted = timeout.Token;
                Exception? failure = null;
                try
                {
                    await Next(context);
                    await response.FinishAsync(timeout.Token);
                }
                catch (Exception ex) when (retryConfig.RetryOnExceptions && attempt < retryConfig.MaxRetries &&
                    !originalAborted.IsCancellationRequested && !response.Committed && !context.Response.HasStarted)
                {
                    failure = ex;
                }
                finally
                {
                    context.RequestAborted = originalAborted;
                    context.Features.Set(originalFeature);
                }

                var destination = proxyFeature?.ProxiedDestination;
                _destinationCoordinator.MarkAttempted(context, destination);


                if (attempt >= retryConfig.MaxRetries || response.Committed || context.Response.HasStarted ||
                    failure == null && !retryConfig.StatusCodes.Contains(context.Response.StatusCode))
                {
                    if (attempt > 0 && !context.Response.HasStarted)
                    {
                        context.Response.Headers["X-Retry-Count"] = attempt.ToString();
                    }

                    await response.CommitAsync(originalAborted);
                    return;
                }
                var budget = context.RequestServices?.GetService<ClusterRetryBudget>() ?? _fallbackBudget;
                if (!budget.TryAcquire(routeConfig.ClusterId))
                {
                    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    await response.CommitAsync(originalAborted);
                    return;
                }
                var retryAfter = context.Response.Headers.RetryAfter.ToString();
                var nextAttempt = attempt + 1;
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers.Clear();
                foreach (var header in originalHeaders)
                {
                    context.Response.Headers[header.Key] = header.Value;
                }

                var delay = (long)retryConfig.BackoffBaseMs * (1 << (nextAttempt - 1)) +
                    (retryConfig.JitterMs > 0 ? (context.RequestServices?.GetService<Random>() ?? Random.Shared).Next(retryConfig.JitterMs) : 0);
                delay = Math.Min(delay, 300000);
                if (RetryConditionHeaderValue.TryParse(retryAfter, out var upstream))
                {
                    var requested = upstream.Delta ?? (upstream.Date - clock.GetUtcNow()) ?? TimeSpan.Zero;
                    var bounded = Math.Clamp(requested.TotalMilliseconds, 0, retryConfig.MaxRetryAfterSeconds * 1000d);
                    delay = Math.Max(delay, (long)bounded);
                }
                _logger.LogDebug("Retry attempt {Attempt} for route {Route}", nextAttempt, routeConfig.RouteId);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 300000)),
                    clock, originalAborted);
                attempt = nextAttempt;
            }
        }
        finally
        {
            context.Request.Body = originalRequestBody;
            context.Request.ContentLength = originalLength;
            context.RequestAborted = originalAborted;
            context.Features.Set(originalFeature);
            if (originalPosition.HasValue)
            {
                originalRequestBody.Position = originalPosition.Value;
            }

            if (requestBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(requestBuffer, clearArray: true);
            }
        }
    }

    public const int MaxRetryBodySizeBytes = 1024 * 1024;

    private bool TryParseRetryConfig(string routeId, out ParsedRetryConfig config)
    {
        config = null!;
        if (!_executionPlans.Current.RetryByRoute.TryGetValue(routeId, out var configured) || !configured.Enabled)
        {
            return false;
        }

        const int retryCapacity = 5;
        var delayCapacity = Math.Max(0, configured.BackoffBaseMs);
        var jitterCapacity = Math.Max(0, configured.BackoffJitterMs);
        var statusCodes = configured.RetryOnStatusCodes
            .Where(code => code is >= 100 and <= 599)
            .ToHashSet();
        if (statusCodes.Count == 0)
        {
            return false;
        }

        config = new ParsedRetryConfig(
            true,
            Math.Clamp(configured.MaxRetries, 0, retryCapacity),
            Math.Clamp(configured.BackoffBaseMs, 0, delayCapacity),
            Math.Clamp(configured.BackoffJitterMs, 0, jitterCapacity),
            Math.Clamp(configured.TimeoutSeconds, 1, 300),
            configured.RetryOnExceptions,
            configured.UseDifferentDestination,
            configured.RetryNonIdempotent,
            Math.Clamp(configured.TotalTimeoutSeconds, 1, 1800),
            Math.Clamp(configured.MaxRetryAfterSeconds, 0, 300),
            statusCodes);
        return true;
    }

    private sealed record ParsedRetryConfig(
        bool Enabled,
        int MaxRetries,
        int BackoffBaseMs,
        int JitterMs,
        int TimeoutSeconds,
        bool RetryOnExceptions,
        bool UseDifferentDestination,
        bool RetryNonIdempotent,
        int TotalTimeoutSeconds,
        int MaxRetryAfterSeconds,
        HashSet<int> StatusCodes);
}
