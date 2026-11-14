using System.Text.Json;
using WafIsh.Services;
using WafIsh.Dashboard.Infrastructure;
using WafIsh.Dashboard.Infrastructure.Auth;
using WafIsh.Dashboard.Modules.Dashboard.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace WafIsh.Dashboard.Modules.Dashboard.Controllers;

/// <summary>
/// Authentication endpoints — login, logout, and two-factor authentication.
/// </summary>
public class DashboardAuthController : Controller
{
    private readonly IDashboardAuthorizationService _authService;
    private readonly AuthenticationAttemptLimiter _attempts;

    private readonly string _defaultLocale;
    private readonly DashboardAuthMode _authMode;
    private readonly string? _jwtSecret;
    private readonly string? _jwtPassword;
    private readonly string? _jwtUsername;
    private readonly bool _enableTwoFactor;
    private readonly string? _twoFactorSecret;
    private readonly int _minPasswordLength;

    // Runtime 2FA state (persisted to file)
    private readonly string _twoFactorStateFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardAuthController"/> class.
    /// </summary>
    public DashboardAuthController(
        IDashboardAuthorizationService authService,
        IOptions<DashboardOptions> dashboardOptions,
        AuthenticationAttemptLimiter attempts)
    {
        _authService = authService;
        _attempts = attempts;

        var opt = dashboardOptions.Value;
        _defaultLocale = opt.Locale;
        _authMode = opt.Auth.AuthMode;
        _jwtSecret = opt.Auth.JwtSecret;
        _jwtPassword = opt.Auth.JwtPassword;
        _jwtUsername = opt.Auth.JwtUsername;
        _enableTwoFactor = opt.Auth.EnableTwoFactor;
        _twoFactorSecret = opt.Auth.TwoFactorSecret;
        _minPasswordLength = opt.Auth.MinPasswordLength;
        _twoFactorStateFile = opt.Auth.TwoFactorStatePath;
    }

    #region Login and logout

    // ── Login / Logout ──

    /// <summary>Login POST — validate credentials and return JWT.</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!_attempts.TryAcquire(HttpContext, "login", out _))
            return StatusCode(429, new { code = 429, message = "Too many authentication attempts. Retry later." });
        if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Json(new { code = 400, message = "Username and password are required" });

        var account = _authMode == DashboardAuthMode.DefaultJwt ? "admin" : _jwtUsername;
        if (string.Equals(request.Username, account, StringComparison.Ordinal) && !_attempts.TryAcquireAccount(HttpContext))
            return StatusCode(429, new { code = 429, message = "Too many authentication attempts. Retry later." });

        if (request.Password.Length < _minPasswordLength)
            return Json(new { code = 400, message = $"Password must be at least {_minPasswordLength} characters" });

        bool valid = _authMode switch
        {
            DashboardAuthMode.CustomJwt =>
                ControlPlaneCredentials.Equal(_jwtUsername, request.Username) && ControlPlaneCredentials.Equal(_jwtPassword, request.Password),
            DashboardAuthMode.DefaultJwt =>
                ControlPlaneCredentials.Equal("admin", request.Username) && ControlPlaneCredentials.Equal(_jwtPassword, request.Password),
            _ => false
        };

        if (!valid)
            return Json(new { code = 401, message = "Invalid credentials" });

        // 2FA verification
        bool twoFactorEnabled; string? twoFactorSecret;
        try { (twoFactorEnabled, twoFactorSecret) = GetTwoFactorState(); }
        catch { return StatusCode(503, new { code = 503, message = "Two-factor authentication is unavailable" }); }
        if (twoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(twoFactorSecret))
                return Json(new { code = 503, message = "Two-factor authentication is unavailable" });
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                return Json(new { code = 202, message = "Two-factor authentication required", requiresTwoFactor = true });

            if (!TotpHelper.ValidateCode(twoFactorSecret, request.TwoFactorCode))
                return Json(new { code = 401, message = "Invalid two-factor code" });
        }

        var token = DashboardJwtHelper.GenerateToken(request.Username, _jwtSecret!);

        Response.Cookies.Append("dashboard_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTime.Now.AddHours(8)
        });

        return Json(new { code = 200, token });
    }

    /// <summary>Logout — clear the auth token cookie.</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("dashboard_token");
        return Json(new { code = 200, message = "Logged out successfully" });
    }

    #endregion

    #region Two-Factor Authentication

    /// <summary>Get 2FA status.</summary>
    [HttpGet("api/2fa/status")]
    public IActionResult GetTwoFactorStatus()
    {
        bool enabled;
        try { (enabled, _) = GetTwoFactorState(); }
        catch { return StatusCode(503, new { code = 503, message = "Two-factor authentication is unavailable" }); }
        return Json(new { code = 200, data = new { enabled, minPasswordLength = _minPasswordLength } });
    }

    /// <summary>Generate a new 2FA secret and QR URL.</summary>
    [HttpGet("api/2fa/setup")]
    public IActionResult SetupTwoFactor()
    {
        var secret = TotpHelper.GenerateSecret();
        var issuer = "Gateway Dashboard";
        var account = _jwtUsername ?? "admin";
        var qrUrl = TotpHelper.BuildOtpAuthUri(issuer, account, secret);
        return Json(new { code = 200, data = new { secret, qrUrl } });
    }

    /// <summary>Verify 2FA code and enable 2FA.</summary>
    [HttpPost("api/2fa/verify")]
    public IActionResult VerifyTwoFactor([FromBody] JsonElement body)
    {
        var code = body.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
        var secret = body.TryGetProperty("secret", out var secretEl) ? secretEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(secret))
            return Json(new { code = 400, message = "Code and secret are required" });

        if (!TotpHelper.ValidateCode(secret, code))
            return Json(new { code = 400, message = "Invalid two-factor code" });

        try { SaveTwoFactorState(true, secret); }
        catch { return StatusCode(503, new { code = 503, message = "Two-factor state could not be saved" }); }
        return Json(new { code = 200, message = "Two-factor authentication enabled" });
    }

    /// <summary>Disable 2FA.</summary>
    [HttpPost("api/2fa/disable")]
    public IActionResult DisableTwoFactor()
    {
        try { SaveTwoFactorState(false, null); }
        catch { return StatusCode(503, new { code = 503, message = "Two-factor state could not be saved" }); }
        return Json(new { code = 200, message = "Two-factor authentication disabled" });
    }

    // ── 2FA State Persistence ──

    private (bool enabled, string? secret) GetTwoFactorState()
    {
        var state = TwoFactorStateStore.Read(_twoFactorStateFile, new(_enableTwoFactor, _twoFactorSecret));
        return (state.Enabled, state.Secret);
    }

    private void SaveTwoFactorState(bool enabled, string? secret)
        => TwoFactorStateStore.Write(_twoFactorStateFile, new(enabled, secret));

    #endregion

    #region Auth Status

    /// <summary>Get current authorization status and mode.</summary>
    [HttpGet("api/auth/status")]
    public IActionResult GetAuthStatus()
    {
        var authModeDescription = _authService.GetAuthModeDescription();
        var isAuthEnabled = _authMode != DashboardAuthMode.None;

        return Json(new
        {
            code = 200,
            data = new
            {
                IsAuthEnabled = isAuthEnabled,
                AuthMode = _authMode.ToString(),
                AuthModeDescription = authModeDescription,
                Locale = _defaultLocale
            }
        });
    }

    #endregion
}
