namespace WafIsh.Infrastructure.Middleware;

/// <summary>
/// Lightweight options for gateway middleware, decoupled from Dashboard configuration.
/// </summary>
public sealed class GatewayMiddlewareOptions
{
    /// <summary>URL prefix for the Dashboard UI. Requests matching this prefix are skipped by gateway middleware.</summary>
    public string[] AdditionalAdministrationPrefixes { get; set; } = [];

    public string RoutePrefix { get; set; } = "apigateway";
}
