using WafIsh.Storage.Entities;

namespace WafIsh.Storage;

/// <summary>Stores plugin schemas and route/cluster configuration bindings.</summary>
public interface IPluginConfigurationRepository
{
    Task<PluginBindingsSnapshot> GetBindingsSnapshotAsync(CancellationToken ct = default)
        => throw new NotSupportedException("This provider does not support atomic binding snapshots.");
    Task<long> CommitBindingsAsync(long expectedRevision, IReadOnlyList<PluginBindingEntity> upserts,
        IReadOnlyList<string> deletes, CancellationToken ct = default)
        => throw new NotSupportedException("This provider does not support atomic binding batches.");

    Task<IReadOnlyList<PluginBindingEntity>> GetBindingsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PluginBindingEntity>> GetBindingsAsync(PluginBindingScope scope, string scopeId, CancellationToken ct = default);
    Task<PluginBindingEntity?> GetBindingAsync(string id, CancellationToken ct = default);
    Task UpsertBindingAsync(PluginBindingEntity binding, CancellationToken ct = default);
    Task<bool> DeleteBindingAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<PluginSchemaEntity>> GetSchemasAsync(CancellationToken ct = default);
    Task<PluginSchemaEntity?> GetSchemaAsync(string pluginId, int schemaVersion, CancellationToken ct = default);
    Task UpsertSchemaAsync(PluginSchemaEntity schema, CancellationToken ct = default);

    // --- Strategy Presets ---

    /// <summary>Get all saved configuration presets.</summary>
    Task<IReadOnlyList<PluginConfigPresetEntity>> GetPresetsAsync(CancellationToken ct = default);

    /// <summary>Get presets for a specific plugin.</summary>
    Task<IReadOnlyList<PluginConfigPresetEntity>> GetPresetsByPluginAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Get a single preset by id.</summary>
    Task<PluginConfigPresetEntity?> GetPresetAsync(string id, CancellationToken ct = default);

    /// <summary>Create or update a preset.</summary>
    Task UpsertPresetAsync(PluginConfigPresetEntity preset, CancellationToken ct = default);

    /// <summary>Delete a preset by id.</summary>
    Task<bool> DeletePresetAsync(string id, CancellationToken ct = default);
}

/// <summary>A consistent binding set and its monotonic storage revision.</summary>
public sealed record PluginBindingsSnapshot(long Revision, IReadOnlyList<PluginBindingEntity> Bindings);

public sealed class BindingRevisionConflictException(long expected, long actual)
    : InvalidOperationException($"Binding revision conflict: expected {expected}, actual {actual}.")
{
    public long ExpectedRevision { get; } = expected;
    public long ActualRevision { get; } = actual;
}
