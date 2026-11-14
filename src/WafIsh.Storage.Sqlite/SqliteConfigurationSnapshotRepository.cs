using Microsoft.Data.Sqlite;
namespace WafIsh.Storage.Sqlite;

/// <summary>Commits the complete configuration in one SQLite transaction.</summary>
public sealed class SqliteConfigurationSnapshotRepository(SqliteConnectionFactory connections)
    : IConfigurationSnapshotRepository
{
    public async Task<ConfigurationSnapshot> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateConnectionAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction(deferred: true);
        var version = await ReadVersion(conn, tx, ct);
        var routes = await Read(conn, tx, "yarp_routes", SqliteRouteRepository.Map, ct);
        var clusters = await Read(conn, tx, "yarp_clusters", SqliteClusterRepository.MapCluster, ct);
        var destinations = await Read(conn, tx, "yarp_destinations", SqliteClusterRepository.MapDestination, ct);
        await tx.CommitAsync(ct);
        return new(version, routes, clusters, destinations);
    }

    public async Task SaveAsync(ConfigurationSnapshot snapshot, long expectedVersion, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateConnectionAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var actual = await ReadVersion(conn, tx, ct);
        if (actual != expectedVersion)
        {
            throw new ConfigurationVersionConflictException(expectedVersion, actual);
        }

        if (snapshot.Version != expectedVersion + 1)
        {
            throw new ArgumentException("The next configuration version must increase by one.", nameof(snapshot));
        }
        // Capture legacy binding identities before rows are replaced or renamed.
        await BindIdentities(conn, tx, ct);
        // Bindings use stable UIDs and have no cascading relationship with these rows.
        await using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM yarp_routes; DELETE FROM yarp_destinations; DELETE FROM yarp_clusters;";
            await clear.ExecuteNonQueryAsync(ct);
        }
        foreach (var cluster in snapshot.Clusters)
        {
            await SqliteClusterRepository.SaveOneAsync(conn, tx, cluster, ct);
        }

        foreach (var route in snapshot.Routes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = SqliteRouteRepository.UpsertSql;
            SqliteRouteRepository.AddParams(cmd, route);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var destination in snapshot.Destinations)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO yarp_destinations (destination_id, cluster_id, address, host, health, healthy, metadata)
                VALUES (@id, @cluster, @address, @host, @health, @healthy, @metadata)
                """;
            cmd.Parameters.AddWithValue("@id", destination.DestinationId);
            cmd.Parameters.AddWithValue("@cluster", destination.ClusterId);
            cmd.Parameters.AddWithValue("@address", destination.Address);
            cmd.Parameters.AddWithValue("@host", destination.Host ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@health", destination.Health ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@healthy", destination.Healthy ? 1 : 0);
            cmd.Parameters.AddWithValue("@metadata", destination.Metadata ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await BindIdentities(conn, tx, ct);
        await using (var bindings = conn.CreateCommand())
        {
            bindings.Transaction = tx;
            bindings.CommandText = """
                UPDATE plugin_bindings SET scope_id = (SELECT route_id FROM yarp_routes WHERE route_uid = plugin_bindings.route_uid),
                    config_version = config_version + 1
                WHERE scope = 1 AND EXISTS (SELECT 1 FROM yarp_routes WHERE route_uid = plugin_bindings.route_uid AND route_id != plugin_bindings.scope_id);
                UPDATE plugin_bindings SET scope_id = (SELECT cluster_id FROM yarp_clusters WHERE cluster_uid = plugin_bindings.cluster_uid),
                    config_version = config_version + 1
                WHERE scope = 2 AND EXISTS (SELECT 1 FROM yarp_clusters WHERE cluster_uid = plugin_bindings.cluster_uid AND cluster_id != plugin_bindings.scope_id);
                """;
            await bindings.ExecuteNonQueryAsync(ct);
        }
        await using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE configuration_version SET version = @version WHERE id = 1";
            update.Parameters.AddWithValue("@version", snapshot.Version);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task BindIdentities(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE plugin_bindings SET route_uid = (SELECT route_uid FROM yarp_routes WHERE route_id = scope_id)
                WHERE scope = 1 AND (route_uid IS NULL OR route_uid = '');
            UPDATE plugin_bindings SET cluster_uid = (SELECT cluster_uid FROM yarp_clusters WHERE cluster_id = scope_id)
                WHERE scope = 2 AND (cluster_uid IS NULL OR cluster_uid = '');
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ReadVersion(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT version FROM configuration_version WHERE id = 1";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<List<T>> Read<T>(SqliteConnection conn, SqliteTransaction tx, string table,
        Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = table switch
        {
            "yarp_routes" => "SELECT * FROM yarp_routes",
            "yarp_clusters" => "SELECT * FROM yarp_clusters",
            "yarp_destinations" => "SELECT * FROM yarp_destinations",
            _ => throw new ArgumentException("Unknown configuration table.", nameof(table))
        };
        var items = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(map(reader));
        }

        return items;
    }
}
