using WafIsh.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WafIsh.Dashboard.Infrastructure.Auth;

/// <summary>
/// Standard dashboard authorization service.
/// Supports all auth modes: None, ApiKey, CustomJwt, DefaultJwt, and custom delegate.
/// </summary>
public sealed class DashboardAuthorizationService : IDashboardAuthorizationService
{
    private readonly DashboardOptions _options;
    private readonly AuthenticationAttemptLimiter _attempts;
    private readonly ControlPlaneAccessPolicy _policy;
    private readonly ILogger<DashboardAuthorizationService> _logger;

    /// <summary>
    /// Creates the authorization service.
    /// </summary>
    public DashboardAuthorizationService(IOptions<DashboardOptions> options, ILogger<DashboardAuthorizationService> logger, ControlPlaneAccessPolicy? policy = null, AuthenticationAttemptLimiter? attempts = null)
    {
        _options = options.Value;
        _attempts = attempts ?? new AuthenticationAttemptLimiter();
        _policy = policy ?? new ControlPlaneAccessPolicy(Options.Create(new WafIsh.Models.ControlPlaneSecurityOptions()));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        if (!_policy.IsAllowed(context, _options.Auth.AuthMode == DashboardAuthMode.None && _options.Auth.AuthorizeRequest == null))
        {
            return false;
        }
        // Priority 1: Custom delegate (highest)
        if (_options.Auth.AuthorizeRequest != null)
        {
            return await _options.Auth.AuthorizeRequest(context);
        }

        if (_options.Auth.AuthMode is DashboardAuthMode.BasicAuth or DashboardAuthMode.ApiKey)
        {
            if (!_attempts.TryAcquire(context, "direct", out var refund))
            {
                return false;
            }

            var valid = _options.Auth.AuthMode == DashboardAuthMode.BasicAuth
                ? ControlPlaneCredentials.ValidateBasic(context.Request.Headers.Authorization,
                    _options.Auth.JwtUsername, _options.Auth.JwtPassword)
                : !string.IsNullOrWhiteSpace(_options.Auth.ApiKey) && CheckApiKey(context);
            if (valid)
            {
                refund();
            }

            return valid;
        }

        // Priority 3: JWT (DefaultJwt / CustomJwt)
        if (_options.Auth.AuthMode is DashboardAuthMode.CustomJwt or DashboardAuthMode.DefaultJwt)
        {
            return CheckJwt(context);
        }

        // AuthMode.None - no authorization required
        return _options.Auth.AuthMode == DashboardAuthMode.None;
    }

    /// <inheritdoc />
    public string GetAuthModeDescription()
    {
        if (_options.Auth.AuthorizeRequest != null)
        {
            return "Custom Delegate";
        }

        return _options.Auth.AuthMode switch
        {
            DashboardAuthMode.None => "None",
            DashboardAuthMode.BasicAuth => "Basic authentication",
            DashboardAuthMode.ApiKey => "API Key",
            DashboardAuthMode.CustomJwt => $"JWT (User: {_options.Auth.JwtUsername ?? "N/A"})",
            DashboardAuthMode.DefaultJwt => "JWT (Admin)",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Checks API key authorization.
    /// </summary>
    private bool CheckApiKey(HttpContext context)
    {
        var apiKey = _options.Auth.ApiKey!;
        var headerName = _options.Auth.ApiKeyHeaderName;

        // Check header
        if (context.Request.Headers.TryGetValue(headerName, out var hv) && 
            hv.Count == 1 && ControlPlaneCredentials.Equal(apiKey, hv[0]))
        {
            return true;
        }

        // Query-string credentials are intentionally rejected because URLs commonly leak
        // through access logs, browser history and referrers.
        return false;
    }

    /// <summary>
    /// Checks JWT authorization.
    /// </summary>
    private bool CheckJwt(HttpContext context)
    {
        var secret = _options.Auth.JwtSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var token = ExtractJwtToken(context);
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var (valid, _) = DashboardJwtHelper.ValidateToken(token, secret);
        if (!valid)
        {
            _logger.LogWarning(
                "Dashboard JWT validation failed for {Path}. RemoteIp: {RemoteIp}",
                context.Request.Path,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }

        return valid;
    }

    private string? ExtractJwtToken(HttpContext context)
    {
        // Authorization header (for XHR/API calls)
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader[7..];
        }

        // SignalR only: browser WebSocket clients cannot always send Authorization headers.
        // Restrict query-string tokens to the two dashboard hub endpoints.
        if (context.Request.Path.Equals($"/{_options.RoutePrefix.Trim('/')}/hubs/traffic", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals($"/{_options.RoutePrefix.Trim('/')}/hubs/overview", StringComparison.OrdinalIgnoreCase))
        {
            var queryToken = context.Request.Query["access_token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(queryToken))
            {
                return queryToken;
            }
        }

        // dashboard_token cookie (for browser page loads)
        if (context.Request.Cookies.TryGetValue("dashboard_token", out var cookieToken)
            && !string.IsNullOrEmpty(cookieToken))
        {
            return cookieToken;
        }

        return null;
    }
}
