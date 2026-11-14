using Microsoft.Data.Sqlite;
using static Wafish.Storage.Sqlite.Migrations.MigrationHelper;
namespace Wafish.Storage.Sqlite.Migrations;

internal sealed class Migration017_ConfigurationVersion : ISchemaMigration
{
    public int Version => 17;
    public string Id => "017_configuration_version";
    public string Description => "Track atomic configuration revisions";
    public Task UpAsync(SqliteConnection conn, SqliteTransaction transaction, CancellationToken ct)
        => ExecuteAsync(conn, transaction, """
            CREATE TABLE configuration_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL);
            INSERT INTO configuration_version VALUES (1, 0);
            """, ct);
}
