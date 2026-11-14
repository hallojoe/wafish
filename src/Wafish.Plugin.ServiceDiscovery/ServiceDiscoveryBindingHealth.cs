namespace Wafish.Plugin.ServiceDiscovery;

/// <summary>One binding's discovery freshness and retry status. Failure details contain no provider credentials.</summary>
public sealed record ServiceDiscoveryBindingHealth(
    string ClusterId, DateTimeOffset? LastSuccessAt, double StaleAgeSeconds,
    int ConsecutiveFailures, string? LastFailureType, bool Stale, bool TrafficRejected, double NextRefreshSeconds);
