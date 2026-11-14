using Microsoft.Data.Sqlite;
using static Wafish.Storage.Sqlite.Migrations.MigrationHelper;
namespace Wafish.Storage.Sqlite.Migrations;

internal sealed class Migration018_RouteActivation : ISchemaMigration
{
    public int Version => 18;
    public string Id => "018_route_activation";
    public string Description => "Preserve route activation across gateway restarts";
    public Task UpAsync(SqliteConnection conn, SqliteTransaction transaction, CancellationToken ct)
        => ExecuteAsync(conn, transaction, "ALTER TABLE yarp_routes ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;", ct);
}
