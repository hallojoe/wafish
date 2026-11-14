namespace Wafish.Models;

/// <summary>
/// Container for all dynamic gateway configurations.
/// </summary>
public class GatewayDynamicConfig
{
    /// <summary>Persisted configuration revision.</summary>
    public long Version { get; set; } = 0;

    /// <summary>Last modified timestamp.</summary>
    public DateTime LastModified { get; set; } = DateTime.Now;

    /// <summary>Dynamic routes (manually added or auto-registered).</summary>
    public List<DynamicRouteConfig> Routes { get; set; } = new();

    /// <summary>Dynamic clusters (manually added or auto-registered).</summary>
    public List<DynamicClusterConfig> Clusters { get; set; } = new();
}
