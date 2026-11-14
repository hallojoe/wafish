using Microsoft.Data.Sqlite;
using static Wafish.Storage.Sqlite.Migrations.MigrationHelper;
namespace Wafish.Storage.Sqlite.Migrations;

internal sealed class Migration022_ConfigurationReceipts : ISchemaMigration
{
    public int Version => 22;
    public string Id => "022_configuration_receipts";
    public string Description => "Atomic configuration idempotency receipts";
    public Task UpAsync(SqliteConnection conn, SqliteTransaction transaction, CancellationToken ct) => ExecuteAsync(conn, transaction,
        """
        CREATE TABLE configuration_receipts (
            request_key TEXT PRIMARY KEY,
            fingerprint TEXT NOT NULL,
            version INTEGER NOT NULL,
            expires_at INTEGER NOT NULL,
            result_json TEXT NULL
        );
        CREATE INDEX ix_configuration_receipts_expiry ON configuration_receipts(expires_at);
        """, ct);
}
