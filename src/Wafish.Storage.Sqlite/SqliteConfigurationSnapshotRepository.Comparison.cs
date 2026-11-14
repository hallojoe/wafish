using Microsoft.Data.Sqlite;
using Wafish.Storage;

namespace Wafish.Storage.Sqlite;

public sealed partial class SqliteConfigurationSnapshotRepository
{
    // Compare persisted fields only. DisplayName is carried by ConfigJson, not a table column.
    private static bool Equal(RouteEntity left, RouteEntity right) =>
        (left.RouteUid, left.ClusterUid, left.ClusterId, left.MatchPath, left.Order, left.Transforms, left.Source,
            left.CreatedBy, StorageTimestamp.Format(left.CreatedAt), StorageTimestamp.Format(left.UpdatedAt), left.Metadata, left.ConfigJson, left.Enabled)
        == (right.RouteUid, right.ClusterUid, right.ClusterId, right.MatchPath, right.Order, right.Transforms, right.Source,
            right.CreatedBy, StorageTimestamp.Format(right.CreatedAt), StorageTimestamp.Format(right.UpdatedAt), right.Metadata, right.ConfigJson, right.Enabled);

    private static bool Equal(ClusterEntity left, ClusterEntity right) =>
        (left.ClusterUid, left.LoadBalancingPolicy, left.HealthCheckConfig, left.Source, left.CreatedBy,
            StorageTimestamp.Format(left.CreatedAt), StorageTimestamp.Format(left.UpdatedAt), Timestamp(left.LastHeartbeat), left.ConfigJson)
        == (right.ClusterUid, right.LoadBalancingPolicy, right.HealthCheckConfig, right.Source, right.CreatedBy,
            StorageTimestamp.Format(right.CreatedAt), StorageTimestamp.Format(right.UpdatedAt), Timestamp(right.LastHeartbeat), right.ConfigJson);

    private static bool Equal(DestinationEntity left, DestinationEntity right) =>
        (left.Address, left.Host, left.Health, left.Healthy, left.Metadata)
        == (right.Address, right.Host, right.Health, right.Healthy, right.Metadata);

    private static string? Timestamp(DateTime? value) => value is { } timestamp ? StorageTimestamp.Format(timestamp) : null;

    private static async Task DeleteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        string id, string? cluster, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        if (cluster is not null) command.Parameters.AddWithValue("@cluster", cluster);
        await command.ExecuteNonQueryAsync(ct);
    }
}
