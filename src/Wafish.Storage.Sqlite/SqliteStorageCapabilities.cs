using Microsoft.Data.Sqlite;
using Wafish.Storage;

namespace Wafish.Storage.Sqlite;

public sealed partial class SqliteConnectionFactory : IStorageCapabilitiesProvider, IStorageBackupProvider
{
    public ValueTask<StorageCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new StorageCapabilities("sqlite", StorageFeature.Encryption | StorageFeature.ConsistentBackup
            | StorageFeature.AtomicConfiguration | StorageFeature.AtomicBindings | StorageFeature.ConfigurationReceipts | StorageFeature.AtomicChangeSets,
            !string.IsNullOrEmpty(new SqliteConnectionStringBuilder(_connectionString).Password)));
    }

    public async Task<StorageBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "wafish-backup-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "backup.db");
        try
        {
            await using var connection = await CreateConnectionAsync(cancellationToken);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO @path";
            command.Parameters.AddWithValue("@path", path);
            await command.ExecuteNonQueryAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var encrypted = (await GetCapabilitiesAsync(cancellationToken)).EncryptionEnabled;
            return new StorageBackup(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous),
                "gateway-store-backup.db", encrypted, () => { Directory.Delete(directory, true); return ValueTask.CompletedTask; });
        }
        catch { Directory.Delete(directory, true); throw; }
    }
}
