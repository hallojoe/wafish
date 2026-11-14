using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WafIsh.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Model;

namespace WafIsh.Plugin.Cache;

public sealed class ResponseCacheMiddleware(RequestDelegate next, ResponseCacheStore cache, GatewayPluginExecutionPlanProvider plans)
{
    // Operational headers do not select a representation. Responses that Vary on these bypass storage.
    private static readonly HashSet<string> ExcludedKeyHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Cache-Control", "Pragma", "X-Request-Id", "traceparent", "tracestate", "baggage" };

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var plan = plans.Current;
        cache.SetGeneration(plan.SnapshotVersion);
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;
        if (request.Method is not ("GET" or "HEAD") || string.IsNullOrWhiteSpace(routeId) ||
            context.User.Identities.Any(i => i.IsAuthenticated) ||
            request.Headers.ContainsKey("Authorization") || request.Headers.ContainsKey("Cookie") ||
            request.Headers.ContainsKey("Range") || request.Headers.Keys.Any(h => h.StartsWith("If-", StringComparison.OrdinalIgnoreCase)) ||
            !plan.ResponseCacheByRoute.TryGetValue(routeId, out var config) || !config.Enabled)
        { await next(context); return; }

        CacheControlHeaderValue? requestControl = null;
        if (request.Headers.ContainsKey("Cache-Control") && !CacheControlHeaderValue.TryParse(request.Headers.CacheControl.ToString(), out requestControl))
        { await next(context); return; }
        if (requestControl is { NoCache: true } or { NoStore: true } || requestControl?.MaxAge == TimeSpan.Zero ||
            request.Headers.Pragma.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase))
        { if (requestControl?.OnlyIfCached == true)
            {
                context.Response.StatusCode = 504;
            }
            else
            {
                await next(context);
            }
            return; }

        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            plan.SnapshotVersion, routeId, request.Scheme, Host = (request.Host.Value ?? "").ToLowerInvariant(),
            PathBase = request.PathBase.Value, Path = request.Path.Value, Query = request.QueryString.Value,
            Headers = request.Headers.Where(h => !ExcludedKeyHeaders.Contains(h.Key))
                .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .Select(h => new { Name = h.Key.ToLowerInvariant(), Values = h.Value.ToArray() }).ToArray()
        })));
        using (var hit = cache.Find(key, requestControl?.MaxAge, requestControl?.MinFresh))
        {
            if (hit != null)
            {
                var entry = hit.Value;
                context.Response.StatusCode = entry.Status;
                foreach (var header in entry.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value;
                }

                context.Response.ContentLength = entry.Length;
                context.Response.Headers.Age = Math.Max(0, (long)(entry.InitialAge + cache.Age(entry)).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                if (request.Method != "HEAD")
                {
                    await context.Response.Body.WriteAsync(entry.Body.AsMemory(0, entry.Length), context.RequestAborted);
                }

                return;
            }
        }
        if (requestControl?.OnlyIfCached == true) { context.Response.StatusCode = StatusCodes.Status504GatewayTimeout; return; }
        // A HEAD miss cannot populate a GET entry with an empty body.
        if (request.Method == "HEAD") { await next(context); return; }
        using var capture = cache.Reserve(config.MaxBodyBytes);
        if (capture == null) { await next(context); return; }
        var original = context.Response.Body;
        using var stream = new BoundedCaptureStream(original, capture);
        context.Response.Body = stream;
        try
        {
            await next(context);
            if (!stream.Complete || context.RequestAborted.IsCancellationRequested ||
                !config.CacheStatusCodes.Contains(context.Response.StatusCode) || context.Response.StatusCode is 204 or 205 or 304 ||
                context.Response.Headers.ContainsKey("Set-Cookie") || context.Response.Headers.ContainsKey("Content-Range"))
            {
                return;
            }

            if (!CacheControlHeaderValue.TryParse(context.Response.Headers.CacheControl.ToString(), out var control) ||
                control is not { Public: true, Private: false, NoStore: false, NoCache: false })
            {
                return;
            }

            var vary = context.Response.Headers.Vary.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (vary.Any(h => h == "*" || ExcludedKeyHeaders.Contains(h)))
            {
                return;
            }

            var now = cache.UtcNow;
            var responseHeaders = context.Response.Headers;
            var age = TimeSpan.Zero;
            if (responseHeaders.ContainsKey("Age"))
            {
                if (!long.TryParse(responseHeaders.Age, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 315360000)
                {
                    return;
                }

                age = TimeSpan.FromSeconds(seconds);
            }
            DateTimeOffset? responseDate = null, responseExpires = null;
            if (responseHeaders.ContainsKey("Date"))
            {
                if (!HeaderUtilities.TryParseDate(responseHeaders.Date.ToString(), out var parsed))
                {
                    return;
                }

                responseDate = parsed;
                if (now - parsed > age)
                {
                    age = now - parsed;
                }
            }
            if (responseHeaders.ContainsKey("Expires"))
            {
                if (!HeaderUtilities.TryParseDate(responseHeaders.Expires.ToString(), out var parsed))
                {
                    return;
                }

                responseExpires = parsed;
            }
            var freshness = control.SharedMaxAge ?? control.MaxAge ??
                (responseExpires is { } expires ? expires - (responseDate ?? now) : TimeSpan.FromSeconds(config.TtlSeconds));
            var lifetime = freshness - age;
            if (lifetime > TimeSpan.FromSeconds(config.TtlSeconds))
            {
                lifetime = TimeSpan.FromSeconds(config.TtlSeconds);
            }

            if (lifetime <= TimeSpan.Zero)
            {
                return;
            }

            if (context.Response.Headers.Count > 128 || context.Response.Headers.Sum(HeaderSize) > ResponseCacheStore.HeaderAllowance)
            {
                return;
            }

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length" };
            foreach (var token in context.Response.Headers.Connection.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                excluded.Add(token);
            }

            var savedHeaders = context.Response.Headers.Where(h => !excluded.Contains(h.Key))
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            cache.Store(capture, key, plan.SnapshotVersion, context.Response.StatusCode, savedHeaders, stream.CapturedLength, lifetime, age);
        }
        finally { context.Response.Body = original; }
    }
    private static long HeaderSize(KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header)
    {
        long size = (long)header.Key.Length * 2 + 128;
        foreach (var value in header.Value)
        {
            size += (long)(value?.Length ?? 0) * 2;
        }

        return size;
    }

}
