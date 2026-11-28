using Microsoft.Data.Sqlite;
using static Wafish.Storage.Sqlite.Migrations.MigrationHelper;
namespace Wafish.Storage.Sqlite.Migrations;

internal sealed class Migration023_OperatorViews : ISchemaMigration
{
    public int Version => 23;
    public string Id => "023_operator_views";
    public string Description => "Private named operator views";
    public Task UpAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) => ExecuteAsync(connection, transaction,
        """
        CREATE TABLE operator_views (
            owner TEXT NOT NULL,
            workspace TEXT NOT NULL,
            id TEXT NOT NULL,
            name TEXT NOT NULL,
            settings_json TEXT NOT NULL,
            is_default INTEGER NOT NULL,
            revision INTEGER NOT NULL,
            PRIMARY KEY(owner, workspace, id)
        );
        CREATE UNIQUE INDEX ix_operator_views_name ON operator_views(owner, workspace, name);
        CREATE UNIQUE INDEX ix_operator_views_default ON operator_views(owner, workspace) WHERE is_default = 1;
        """, cancellationToken);
}
