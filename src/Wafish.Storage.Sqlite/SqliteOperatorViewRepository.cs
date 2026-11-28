using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wafish.Storage;

namespace Wafish.Storage.Sqlite;

/// <summary>Transactional, owner-scoped preference storage. Independent of gateway configuration publication.</summary>
public sealed class SqliteOperatorViewRepository(SqliteConnectionFactory connections) : IOperatorViewRepository
{
    public async Task<IReadOnlyList<OperatorView>> ListAsync(string owner, string workspace, CancellationToken cancellationToken = default)
    {
        ValidateScope(owner, workspace);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, owner, workspace);
        command.CommandText = "SELECT id, name, settings_json, is_default, revision FROM operator_views WHERE owner = @owner AND workspace = @workspace ORDER BY name, id LIMIT 50";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var views = new List<OperatorView>();
        while (await reader.ReadAsync(cancellationToken))
            views.Add(new(Guid.ParseExact(reader.GetString(0), "N"), workspace, reader.GetString(1),
                JsonSerializer.Deserialize<OperatorViewSettings>(reader.GetString(2))!, reader.GetBoolean(3), reader.GetInt64(4)));
        return views;
    }

    public async Task<OperatorViewWriteResult> SaveAsync(string owner, OperatorView view, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateScope(owner, view.Workspace);
        if (!OperatorViewRules.IsValid(view) || expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentException("Invalid operator view.");
        view = view with { Settings = view.Settings with { Columns = view.Settings.Columns.ToArray() } };
        var settings = JsonSerializer.Serialize(view.Settings);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = Command(connection, transaction, owner, view.Workspace, view.Id);
        command.CommandText = "SELECT revision FROM operator_views WHERE owner = @owner AND workspace = @workspace AND id = @id";
        var revision = await command.ExecuteScalarAsync(cancellationToken);
        if (revision is null ? expectedRevision != 0 : Convert.ToInt64(revision) != expectedRevision)
            return new(OperatorViewWriteOutcome.Conflict);
        if (revision is null)
        {
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN owner = @owner THEN 1 ELSE 0 END), 0) FROM operator_views";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) >= OperatorViewRules.MaximumTotal || reader.GetInt64(1) >= OperatorViewRules.MaximumPerOwner)
                return new(OperatorViewWriteOutcome.Capacity);
        }
        command.Parameters.AddWithValue("@name", view.Name);
        command.CommandText = "SELECT COUNT(*) FROM operator_views WHERE owner = @owner AND workspace = @workspace AND name = @name AND id != @id";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0) return new(OperatorViewWriteOutcome.NameConflict);
        if (view.IsDefault)
        {
            command.CommandText = "UPDATE operator_views SET is_default = 0, revision = revision + 1 WHERE owner = @owner AND workspace = @workspace AND id != @id AND is_default = 1";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        command.CommandText = """
            INSERT INTO operator_views(owner, workspace, id, name, settings_json, is_default, revision)
            VALUES(@owner, @workspace, @id, @name, @settings, @default, @revision)
            ON CONFLICT(owner, workspace, id) DO UPDATE SET name = @name, settings_json = @settings, is_default = @default, revision = @revision
            """;
        command.Parameters.AddWithValue("@settings", settings);
        command.Parameters.AddWithValue("@default", view.IsDefault);
        command.Parameters.AddWithValue("@revision", checked(expectedRevision + 1));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OperatorViewWriteOutcome.Saved, view with { Revision = expectedRevision + 1 });
    }

    public async Task<OperatorViewWriteResult> DeleteAsync(string owner, string workspace, Guid id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateScope(owner, workspace);
        if (id == Guid.Empty || expectedRevision < 1) throw new ArgumentException("Invalid operator view revision.");
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = Command(connection, transaction, owner, workspace, id);
        command.CommandText = "SELECT revision FROM operator_views WHERE owner = @owner AND workspace = @workspace AND id = @id";
        var revision = await command.ExecuteScalarAsync(cancellationToken);
        if (revision is null) return new(OperatorViewWriteOutcome.NotFound);
        if (Convert.ToInt64(revision) != expectedRevision) return new(OperatorViewWriteOutcome.Conflict);
        command.CommandText = "DELETE FROM operator_views WHERE owner = @owner AND workspace = @workspace AND id = @id";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OperatorViewWriteOutcome.Deleted);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await connections.CreateConnectionAsync(cancellationToken);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static void ValidateScope(string owner, string workspace)
    { if (!OperatorViewRules.IsOwner(owner) || !OperatorViewRules.IsWorkspace(workspace)) throw new ArgumentException("Invalid operator view scope."); }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string owner, string workspace, Guid? id = null)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("@owner", owner); command.Parameters.AddWithValue("@workspace", workspace);
        if (id is not null) command.Parameters.AddWithValue("@id", id.Value.ToString("N"));
        return command;
    }
}
