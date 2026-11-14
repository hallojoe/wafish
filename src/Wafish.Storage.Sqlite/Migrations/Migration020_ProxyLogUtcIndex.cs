using Microsoft.Data.Sqlite;
using static Wafish.Storage.Sqlite.Migrations.MigrationHelper;
namespace Wafish.Storage.Sqlite.Migrations;

/// <summary>Indexes existing and future log timestamps by their UTC instant without rewriting original text.</summary>
internal sealed class Migration020_ProxyLogUtcIndex : ISchemaMigration
{
    public int Version => 20;
    public string Id => "020_proxy_log_utc_index";
    public string Description => "Index proxy log time ranges by UTC instant";
    public Task UpAsync(SqliteConnection conn, SqliteTransaction transaction, CancellationToken ct)
        => ExecuteAsync(conn, transaction,
            "CREATE INDEX IF NOT EXISTS ix_proxy_logs_meta_utc ON proxy_logs_meta(julianday(Timestamp));", ct);
}
