using Microsoft.AspNetCore.Http;

namespace Wafish.Dashboard.Infrastructure;

/// <summary>
/// Authentication options for Dashboard access.
/// Extracted from DashboardOptions to separate auth concerns from other configuration.
/// </summary>
public class DashboardAuthOptions
{
    /// <summary>Optional WebAuthn login for the configured JWT account.</summary>
    public DashboardPasskeyOptions Passkeys { get; set; } = new();
    /// <summary>Permissions for shared credentials. Null preserves administrator access; an empty array grants none.</summary>
    public Auth.DashboardPermission[]? Permissions { get; set; }
    /// <summary>Optional per-operation rule, evaluated after successful authentication.</summary>
    public Func<HttpContext, Auth.DashboardPermission, CancellationToken, ValueTask<bool>>? AuthorizeOperation { get; set; }

    /// <summary>
    /// Authorization mode for accessing the dashboard.
    /// </summary>
    public DashboardAuthMode AuthMode { get; set; } = DashboardAuthMode.None;

    /// <summary>API key value. Clients pass it via the configured header (default: X-Api-Key).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Header name for ApiKey mode. Default: X-Api-Key.</summary>
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    /// <summary>JWT signing secret. Auto-generated if not set (invalidated on restart).</summary>
    public string? JwtSecret { get; set; }

    /// <summary>Username for CustomJwt mode.</summary>
    public string? JwtUsername { get; set; }

    /// <summary>Password for JWT login (required for both CustomJwt and DefaultJwt).</summary>
    public string? JwtPassword { get; set; }

    /// <summary>
    /// Enable Two-Factor Authentication (TOTP) for JWT login.
    /// Default: false.
    /// </summary>
    public bool EnableTwoFactor { get; set; }

    /// <summary>
    /// Base32-encoded TOTP shared secret for 2FA.
    /// If missing while EnableTwoFactor is true, login is rejected.
    /// </summary>
    public string? TwoFactorSecret { get; set; }

    /// <summary>Persistent state path. Give each gateway its own protected file.</summary>
    public string TwoFactorStatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "twofactor-state.json");

    /// <summary>
    /// Minimum password length for JWT login. Default: 6.
    /// </summary>
    public int MinPasswordLength { get; set; } = 6;

    /// <summary>Custom auth delegate. If set, takes precedence over all other auth modes.</summary>
    public Func<HttpContext, Task<bool>>? AuthorizeRequest { get; set; }
}
