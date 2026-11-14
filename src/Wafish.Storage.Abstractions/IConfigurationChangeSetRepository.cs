using Wafish.Storage.Entities;

namespace Wafish.Storage;

/// <summary>Optional atomic replacement of configuration, bindings, and an idempotency receipt.</summary>
public interface IConfigurationChangeSetRepository : IIdempotentConfigurationSnapshotRepository
{
    /// <summary>Checks both revisions before any write. Returns the committed binding revision.</summary>
    Task<long> SaveChangeSetAsync(ConfigurationSnapshot snapshot, long expectedVersion, long expectedBindingRevision,
        IReadOnlyList<PluginBindingEntity> bindings, string key, string fingerprint, CancellationToken cancellationToken = default);
}
