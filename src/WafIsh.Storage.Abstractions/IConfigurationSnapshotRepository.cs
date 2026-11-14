namespace WafIsh.Storage;

/// <summary>Atomic route, cluster, destination, and version storage.</summary>
public interface IConfigurationSnapshotRepository
{
    Task<ConfigurationSnapshot> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(ConfigurationSnapshot snapshot, long expectedVersion, CancellationToken ct = default);
}

public sealed record ConfigurationSnapshot(long Version, IReadOnlyList<RouteEntity> Routes,
    IReadOnlyList<ClusterEntity> Clusters, IReadOnlyList<DestinationEntity> Destinations);

public sealed class ConfigurationVersionConflictException(long expected, long actual)
    : Exception($"Configuration version conflict: expected {expected}, stored {actual}.")
{
    public long ExpectedVersion { get; } = expected;
    public long ActualVersion { get; } = actual;
}
