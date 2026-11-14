using Microsoft.AspNetCore.Http;

namespace Wafish.Infrastructure;

/// <summary>
/// Unified client IP resolution from HTTP headers.
/// Centralizes the X-Forwarded-For / X-Real-IP parsing that was previously duplicated across
/// WAF middleware, rate-limit middleware, controllers, and load-balancing policies.
/// </summary>
public static class ClientIpResolver
{
    /// <summary>
    /// Resolves the client IP established by ASP.NET Core's trusted forwarded-header middleware.
    /// Raw forwarding headers are deliberately ignored here because they are attacker-controlled
    /// unless the application has validated the immediate proxy against KnownProxies/KnownNetworks.
    /// </summary>
    public static string? GetClientIp(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Resolves the connection-facing IP, preferring the direct connection endpoint.
    /// This is intended for load-balancing scenarios (e.g. sticky sessions) where the
    /// actual network peer is more relevant than a potentially spoofed forwarded header.
    /// </summary>
    public static string? GetConnectionIp(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString();
}
