using Microsoft.Data.Sqlite;
using static WafIsh.Storage.Sqlite.Migrations.MigrationHelper;
namespace WafIsh.Storage.Sqlite.Migrations;

internal sealed class Migration019_BindingRevision : ISchemaMigration
{
    public int Version => 19;
    public string Id => "019_binding_revision";
    public string Description => "Atomic binding revisions for all storage writers";
    public Task UpAsync(SqliteConnection conn, SqliteTransaction transaction, CancellationToken ct)
        => ExecuteAsync(conn, transaction, """
            CREATE TABLE plugin_binding_revision (id INTEGER PRIMARY KEY CHECK(id = 1), revision INTEGER NOT NULL);
            INSERT INTO plugin_binding_revision VALUES(1, 0);
            CREATE TRIGGER plugin_binding_insert AFTER INSERT ON plugin_bindings BEGIN
                UPDATE plugin_binding_revision SET revision = revision + 1 WHERE id = 1; END;
            CREATE TRIGGER plugin_binding_update AFTER UPDATE ON plugin_bindings BEGIN
                UPDATE plugin_binding_revision SET revision = revision + 1 WHERE id = 1; END;
            CREATE TRIGGER plugin_binding_delete AFTER DELETE ON plugin_bindings BEGIN
                UPDATE plugin_binding_revision SET revision = revision + 1 WHERE id = 1; END;
            """, ct);
}
