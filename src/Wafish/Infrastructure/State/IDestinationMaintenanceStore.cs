namespace Wafish.Infrastructure.State;

public sealed record DestinationMaintenanceWindow(string ClusterId, string DestinationId, string Actor, string Reason,
    DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, bool AllowRecoveryProbes);

/// <summary>Process-local maintenance windows, independent of circuit failure statistics.</summary>
public interface IDestinationMaintenanceStore
{
    DestinationMaintenanceWindow Set(string clusterId, string destinationId, string actor, string reason, TimeSpan duration, bool allowRecoveryProbes);
    bool Remove(string clusterId, string destinationId);
    IReadOnlyList<DestinationMaintenanceWindow> GetAll();
    bool AllowsTraffic(string clusterId, string destinationId, bool recoveryProbe = false);
}

/// <summary>Read-only recovery eligibility. This never consumes a half-open permit.</summary>
public interface ICircuitRecoveryState
{
    bool IsRecoveryProbeEligible(string clusterId, string destinationId);
}
