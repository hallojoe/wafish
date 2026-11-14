using Microsoft.Data.Sqlite;
using Wafish.Storage.Entities;

namespace Wafish.Storage.Sqlite;

public sealed partial class SqliteConfigurationSnapshotRepository
{
    public Task<long> SaveChangeSetAsync(ConfigurationSnapshot snapshot, long expectedVersion, long expectedBindingRevision,
        IReadOnlyList<PluginBindingEntity> bindings, string key, string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || fingerprint.Length != 64)
            throw new ArgumentException("Invalid configuration receipt identity.");
        return SaveCoreAsync(snapshot, expectedVersion, new(key, fingerprint, snapshot.Version, null), cancellationToken,
            expectedBindingRevision, bindings);
    }

    private static async Task<long> ReadBindingRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM plugin_binding_revision WHERE id = 1";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
