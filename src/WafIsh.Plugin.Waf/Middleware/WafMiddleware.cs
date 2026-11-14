using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WafIsh.Infrastructure.Middleware;
using WafIsh.Plugins;
using WafIsh.Services;
using WafIsh.Models;
using WafIsh.Infrastructure;
using Yarp.ReverseProxy.Model;

namespace WafIsh.Plugin.Waf;

/// <summary>
/// Web Application Firewall middleware — orchestrator.
/// Delegates detection to the <see cref="IWafRuleChecker"/> chain and records security events.
/// Business settings come exclusively from the current route's enabled plugin binding snapshot.
/// </summary>
public sealed class WafMiddleware : GatewayMiddlewareBase
{
    private readonly ILogger<WafMiddleware> _logger;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;

    /// <summary>
    /// Stateless checker chain. Each checker is a singleton — thread-safe, no per-request state.
    /// Order matters: IP → size/headers → path traversal → SQL injection → XSS.
    /// </summary>
    private static readonly IWafRuleChecker[] Checkers =
    [
        IpAccessRuleChecker.Instance,
        RequestSizeRuleChecker.Instance,
        PathTraversalRuleChecker.Instance,
        SqlInjectionRuleChecker.Instance,
        XssRuleChecker.Instance,
    ];

    public WafMiddleware(
        RequestDelegate next,
        ILogger<WafMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions,
        IGatewayPluginManager pluginManager,
        GatewayPluginExecutionPlanProvider executionPlans)
        : base(next, dashOptions, pluginManager)
    {
        _logger = logger;
        _executionPlans = executionPlans;
        DashPrefix = "/" + dashOptions.Value.RoutePrefix.Trim('/');
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId
            ?? context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
        if (!TryResolveBindingOptions(routeId, out var opts))
        {
            await Next(context);
            return;
        }

        // Build shared context with decoded inputs
        var rawPath = GetRawRequestPath(context);
        var decodedQuery = DecodeQuery(context.Request.QueryString.Value);

        var wafContext = new WafCheckContext
        {
            HttpContext = context,
            Options = opts,
            DecodedQueryString = decodedQuery,
            RawRequestPath = rawPath
        };

        var inspect = opts.EnableSqlInjectionDetection || opts.EnableXssDetection;
        if (opts.EnableRequestSizeValidation || inspect)
        {
            if (inspect && ((!string.IsNullOrEmpty(context.Request.Headers.ContentEncoding) &&
                !string.Equals(context.Request.Headers.ContentEncoding, "identity", StringComparison.OrdinalIgnoreCase)) ||
                (context.Request.GetTypedHeaders().ContentType?.Charset.Value is { Length: > 0 } charset &&
                    !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))))
            {
                await BlockRequest(context, "Body inspection requires uncompressed UTF-8 content", StatusCodes.Status415UnsupportedMediaType);
                return;
            }
            try
            {
                var body = await ReadAndDecodeBodyAsync(context, opts, inspect);
                if (body.TooLarge)
                {
                    LogWafBlock(context, "RequestSizeBlocked", "Request exceeds the body or inspection limit");
                    await BlockRequest(context, "Request exceeds the body or inspection limit", StatusCodes.Status413PayloadTooLarge);
                    return;
                }
                wafContext.DecodedBodyText = body.Text;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (DecoderFallbackException)
            {
                await BlockRequest(context, "Request body is not valid UTF-8", StatusCodes.Status400BadRequest);
                return;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "WAF request inspection failed");
                await BlockRequest(context, "Request inspection is unavailable", StatusCodes.Status503ServiceUnavailable);
                return;
            }
        }

        // Run checker chain
        foreach (var checker in Checkers)
        {
            WafCheckResult result;
            try { result = checker.Check(wafContext); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                await BlockRequest(context, "Request inspection timed out", StatusCodes.Status503ServiceUnavailable);
                return;
            }
            if (result.IsBlocked)
            {
                LogWafBlock(context, result.EventType!, result.Details);
                await BlockRequest(context, result.EventType switch
                {
                    "IpBlocked" => "Access denied",
                    "RequestSizeBlocked" => "Request entity too large",
                    "MalformedHeadersBlocked" => "Too many headers",
                    "UriTooLongBlocked" => "URI too long",
                    _ => "Blocked by security policy"
                });
                return;
            }
        }

        await Next(context);
    }

    #region Private helpers

    private bool TryResolveBindingOptions(string? routeId, out WafBindingOptions options)
    {
        options = null!;
        if (string.IsNullOrWhiteSpace(routeId))
        {
            return false;
        }

        if (!_executionPlans.Current.WafByRoute.TryGetValue(routeId, out var configured) || !configured.Enabled)
        {
            return false;
        }

        options = configured;
        return true;
    }

    private static string GetRawRequestPath(HttpContext context)
    {
        var raw = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
        {
            return context.Request.Path.Value ?? "";
        }

        var qIdx = raw.IndexOf('?');
        return qIdx >= 0 ? raw[..qIdx] : raw;
    }

    private static string DecodeQuery(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
        {
            return "";
        }

        var trimmed = rawQuery.TrimStart('?');
        return string.IsNullOrEmpty(trimmed) ? "" : WebUtility.UrlDecode(trimmed);
    }

    public const int MaxInspectionBodyBytes = 100 * 1024;

    private static async Task<(bool TooLarge, string? Text)> ReadAndDecodeBodyAsync(
        HttpContext context, WafBindingOptions options, bool inspect)
    {
        var limit = options.EnableRequestSizeValidation ? Math.Max(0, options.MaxRequestBodySize) : long.MaxValue;
        if (inspect)
        {
            limit = Math.Min(limit, MaxInspectionBodyBytes);
        }

        if (context.Request.ContentLength > limit)
        {
            return (true, null);
        }

        context.Request.EnableBuffering(bufferThreshold: 64 * 1024,
            bufferLimit: limit == long.MaxValue ? limit : limit + 1);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long total = 0;
            while (true)
            {
                var size = limit - total >= 8192 ? 8192 : (int)(limit - total + 1);
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, size), context.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > limit)
                {
                    return (true, null);
                }
            }
            if (!inspect || total == 0)
            {
                return (false, null);
            }

            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var text = await reader.ReadToEndAsync(context.RequestAborted);
            if (context.Request.ContentType?.Contains("urlencoded", StringComparison.OrdinalIgnoreCase) == true)
            {
                text = WebUtility.UrlDecode(text);
            }

            return (false, text);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }
    }

    private void LogWafBlock(HttpContext context, string eventType, string? details)
    {
        var clientIp = ClientIpResolver.GetClientIp(context);
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config.RouteId;
        _logger.LogWarning("WAF rejected request: Rule={Rule}, Route={Route}, Trace={Trace}, IP={Ip}",
            eventType, routeId, context.TraceIdentifier, clientIp);

    }

    private static async Task BlockRequest(HttpContext context, string message, int status = StatusCodes.Status403Forbidden)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "Forbidden", message, waf = true }, cancellationToken: context.RequestAborted);
    }

    #endregion
}
