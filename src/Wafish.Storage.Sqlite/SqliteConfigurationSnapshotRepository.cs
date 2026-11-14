using Microsoft.Data.Sqlite;
using Wafish.Storage;
namespace Wafish.Storage.Sqlite;

/// <summary>Commits the complete configuration in one SQLite transaction.</summary>
public sealed partial class SqliteConfigurationSnapshotRepository(SqliteConnectionFactory connections)
    : IConfigurationChangeSetRepository
{
    public async Task<ConfigurationSnapshot> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateConnectionAsync(ct);
        await conn.OpenAsync(ct);
        return await ReadSnapshotAsync(conn, ct);
    }

    internal static async Task<ConfigurationSnapshot> ReadSnapshotAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var tx = conn.BeginTransaction(deferred: true);
        var version = await ReadVersion(conn, tx, ct);
        var routes = await Read(conn, tx, "yarp_routes", SqliteRouteRepository.Map, ct);
        var clusters = await Read(conn, tx, "yarp_clusters", SqliteClusterRepository.MapCluster, ct);
        var destinations = await Read(conn, tx, "yarp_destinations", SqliteClusterRepository.MapDestination, ct);
        await tx.CommitAsync(ct);
        return new(version, routes, clusters, destinations);
    }

    public Task SaveAsync(ConfigurationSnapshot snapshot, long expectedVersion, CancellationToken ct = default)
        => SaveCoreAsync(snapshot, expectedVersion, null, ct);

    private async Task<long> SaveCoreAsync(ConfigurationSnapshot snapshot, long expectedVersion, ConfigurationMutationReceipt? receipt, CancellationToken ct,
        long? expectedBindingRevision = null, IReadOnlyList<Wafish.Storage.Entities.PluginBindingEntity>? bindings = null)
    {
        await using var conn = await connections.CreateConnectionAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        if (expectedBindingRevision is { } expectedBindings)
        {
            var actualBindings = await ReadBindingRevisionAsync(conn, tx, ct);
            if (actualBindings != expectedBindings) throw new BindingRevisionConflictException(expectedBindings, actualBindings);
        }
        if (receipt is not null) await InsertReceiptAsync(conn, tx, receipt, ct);
        var actual = await ReadVersion(conn, tx, ct);
        if (actual != expectedVersion)
        {
            throw new ConfigurationVersionConflictException(expectedVersion, actual);
        }

        if (snapshot.Version != expectedVersion + 1)
        {
            throw new ArgumentException("The next configuration version must increase by one.", nameof(snapshot));
        }
        var previousRoutes = await Read(conn, tx, "yarp_routes", SqliteRouteRepository.Map, ct);
        var previousClusters = await Read(conn, tx, "yarp_clusters", SqliteClusterRepository.MapCluster, ct);
        var previousDestinations = await Read(conn, tx, "yarp_destinations", SqliteClusterRepository.MapDestination, ct);
        var routes = snapshot.Routes.ToDictionary(route => route.RouteId, StringComparer.Ordinal);
        var clusters = snapshot.Clusters.ToDictionary(cluster => cluster.ClusterId, StringComparer.Ordinal);
        var destinations = snapshot.Destinations.ToDictionary(destination => (destination.ClusterId, destination.DestinationId));
        var retainedRoutes = previousRoutes.Where(old => routes.TryGetValue(old.RouteId, out var next) && Equal(old, next))
            .Select(route => route.RouteId).ToHashSet(StringComparer.Ordinal);
        var retainedClusters = previousClusters.Where(old => clusters.TryGetValue(old.ClusterId, out var next) && Equal(old, next))
            .Select(cluster => cluster.ClusterId).ToHashSet(StringComparer.Ordinal);
        var retainedDestinations = previousDestinations.Where(old => retainedClusters.Contains(old.ClusterId)
            && destinations.TryGetValue((old.ClusterId, old.DestinationId), out var next) && Equal(old, next))
            .Select(destination => (destination.ClusterId, destination.DestinationId)).ToHashSet();
        // Capture legacy binding identities before rows are replaced or renamed.
        await BindIdentities(conn, tx, ct);
        // Delete changed identities before inserts so key swaps remain atomic.
        // Reinsert destinations of changed clusters because cluster deletion can cascade.
        foreach (var old in previousRoutes.Where(route => !retainedRoutes.Contains(route.RouteId)))
            await DeleteAsync(conn, tx, "DELETE FROM yarp_routes WHERE route_id = @id", old.RouteId, null, ct);
        foreach (var old in previousDestinations.Where(destination => !retainedDestinations.Contains((destination.ClusterId, destination.DestinationId))))
            await DeleteAsync(conn, tx, "DELETE FROM yarp_destinations WHERE destination_id = @id AND cluster_id = @cluster", old.DestinationId, old.ClusterId, ct);
        foreach (var old in previousClusters.Where(cluster => !retainedClusters.Contains(cluster.ClusterId)))
            await DeleteAsync(conn, tx, "DELETE FROM yarp_clusters WHERE cluster_id = @id", old.ClusterId, null, ct);
        foreach (var cluster in snapshot.Clusters.Where(cluster => !retainedClusters.Contains(cluster.ClusterId)))
        {
            await SqliteClusterRepository.SaveOneAsync(conn, tx, cluster, ct);
        }

        foreach (var route in snapshot.Routes.Where(route => !retainedRoutes.Contains(route.RouteId)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = SqliteRouteRepository.UpsertSql;
            SqliteRouteRepository.AddParams(cmd, route);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var destination in snapshot.Destinations.Where(destination => !retainedDestinations.Contains((destination.ClusterId, destination.DestinationId))))
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
        await using (var renamedBindings = conn.CreateCommand())
        {
            renamedBindings.Transaction = tx;
            renamedBindings.CommandText = """
                UPDATE plugin_bindings SET scope_id = (SELECT route_id FROM yarp_routes WHERE route_uid = plugin_bindings.route_uid),
                    config_version = config_version + 1
                WHERE scope = 1 AND EXISTS (SELECT 1 FROM yarp_routes WHERE route_uid = plugin_bindings.route_uid AND route_id != plugin_bindings.scope_id);
                UPDATE plugin_bindings SET scope_id = (SELECT cluster_id FROM yarp_clusters WHERE cluster_uid = plugin_bindings.cluster_uid),
                    config_version = config_version + 1
                WHERE scope = 2 AND EXISTS (SELECT 1 FROM yarp_clusters WHERE cluster_uid = plugin_bindings.cluster_uid AND cluster_id != plugin_bindings.scope_id);
                """;
            await renamedBindings.ExecuteNonQueryAsync(ct);
        }
        await using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE configuration_version SET version = @version WHERE id = 1";
            update.Parameters.AddWithValue("@version", snapshot.Version);
            await update.ExecuteNonQueryAsync(ct);
        }
        if (bindings is not null)
        {
            await using var delete = conn.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM plugin_bindings";
            await delete.ExecuteNonQueryAsync(ct);
            foreach (var binding in bindings)
                await SqlitePluginConfigurationRepository.UpsertAsync(conn, tx, binding, ct);
        }
        var bindingRevision = await ReadBindingRevisionAsync(conn, tx, ct);
        await tx.CommitAsync(ct);
        return bindingRevision;
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
