namespace WafIsh.Models;

/// <summary>
/// Unified control-plane security options for Gateway REST API, gRPC registration, and Dashboard.
/// Binds from <c>Gateway:Security:ControlPlane</c>.
/// </summary>
public class ControlPlaneSecurityOptions
{
    public const string SectionName = "Gateway:Security:ControlPlane";

    /// <summary>Auth mode: None, ApiKey, BasicAuth, DefaultJwt, CustomJwt.</summary>
    public string? AuthMode { get; set; }

    /// <summary>Permit anonymous requests only from direct loopback connections.</summary>
    public bool AllowAnonymousLoopback { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? JwtSecret { get; set; }
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    /// <summary>Legacy option. Enabling API keys in query strings is rejected.</summary>
    public bool AllowApiKeyInQuery { get; set; } = false;

    /// <summary>Legacy compatibility option. All environments now enforce the shared access policy.</summary>
    public bool RequireAuthInProduction { get; set; } = true;
}
