using Microsoft.Data.Sqlite;
using Wafish.Storage;

namespace Wafish.Storage.Sqlite;

public sealed partial class SqliteConfigurationSnapshotRepository
{
    public async Task<ConfigurationMutationReceipt?> GetReceiptAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.CreateConnectionAsync(cancellationToken);
        await conn.OpenAsync(cancellationToken);
        await using var query = conn.CreateCommand();
        query.CommandText = "SELECT fingerprint, version, result_json FROM configuration_receipts WHERE request_key=@key AND expires_at>@now";
        query.Parameters.AddWithValue("@key", key);
        query.Parameters.AddWithValue("@now", connections.Clock.GetUtcNow().ToUnixTimeSeconds());
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(key, reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
    }

    public Task SaveWithReceiptAsync(ConfigurationSnapshot snapshot, long expectedVersion, string key, string fingerprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 || fingerprint.Length != 64) throw new ArgumentException("Invalid configuration receipt identity.");
        return SaveCoreAsync(snapshot, expectedVersion, new(key, fingerprint, snapshot.Version, null), cancellationToken);
    }

    public async Task CompleteReceiptAsync(string key, string resultJson, CancellationToken cancellationToken = default)
    {
        if (resultJson.Length > 16384) throw new ArgumentException("Configuration receipt result exceeds its limit.");
        await using var conn = await connections.CreateConnectionAsync(cancellationToken);
        await conn.OpenAsync(cancellationToken);
        await using var update = conn.CreateCommand();
        update.CommandText = "UPDATE configuration_receipts SET result_json=@result WHERE request_key=@key AND result_json IS NULL";
        update.Parameters.AddWithValue("@key", key); update.Parameters.AddWithValue("@result", resultJson);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertReceiptAsync(SqliteConnection conn, SqliteTransaction transaction, ConfigurationMutationReceipt receipt, CancellationToken ct)
    {
        await using var command = conn.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM configuration_receipts WHERE expires_at<=@now; SELECT count(*) FROM configuration_receipts WHERE request_key=@key";
        var now = connections.Clock.GetUtcNow().ToUnixTimeSeconds();
        command.Parameters.AddWithValue("@now", now); command.Parameters.AddWithValue("@key", receipt.Key);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0) throw new ConfigurationReceiptExistsException(receipt.Key);
        command.CommandText = "SELECT count(*) FROM configuration_receipts";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) >= 10_000) throw new InvalidOperationException("Configuration receipt capacity reached. Retry after existing receipts expire.");
        command.CommandText = "INSERT INTO configuration_receipts(request_key,fingerprint,version,expires_at) VALUES(@key,@hash,@version,@expiry)";
        command.Parameters.AddWithValue("@hash", receipt.Fingerprint); command.Parameters.AddWithValue("@version", receipt.Version);
        command.Parameters.AddWithValue("@expiry", now + 86400);
        await command.ExecuteNonQueryAsync(ct);
    }
}
