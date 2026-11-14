using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Sqlite;

namespace Wafish.ReactUi.Host;

/// <summary>Process-selected faults; no control endpoints are registered.</summary>
internal sealed class UiTestFaults(string profile)
{
    public volatile bool Armed;
    public bool FailActivation => Armed && profile == "activation-failure";
    public async Task BeforeSaveAsync(SqliteConfigurationSnapshotRepository inner, CancellationToken cancellationToken)
    {
        if (!Armed) return;
        if (profile == "version-conflict")
        {
            var current = await inner.LoadAsync(cancellationToken);
            await inner.SaveAsync(current with { Version = current.Version + 1 }, current.Version, cancellationToken);
            throw new ConfigurationVersionConflictException(current.Version, current.Version + 1);
        }
        if (profile == "delayed-failure") await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        if (profile is "storage-failure" or "delayed-failure") throw new IOException("Fixture storage write failed.");
    }
}

internal sealed class UiTestSnapshotRepository(SqliteConfigurationSnapshotRepository inner, UiTestFaults faults) : IConfigurationSnapshotRepository
{
    public Task<ConfigurationSnapshot> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
    public async Task SaveAsync(ConfigurationSnapshot snapshot, long expectedVersion, CancellationToken ct = default)
    {
        await faults.BeforeSaveAsync(inner, ct);
        await inner.SaveAsync(snapshot, expectedVersion, ct);
    }
}

internal sealed class UiTestSnapshotPublisher(UiTestFaults faults) : IGatewaySnapshotPublisher
{
    private readonly GatewaySnapshotPublisher _inner = new();
    public GatewaySnapshot Current => _inner.Current;
    public GatewaySnapshot Publish(GatewaySnapshot snapshot) => faults.FailActivation
        ? throw new InvalidOperationException("Fixture activation failed.") : _inner.Publish(snapshot);
}
