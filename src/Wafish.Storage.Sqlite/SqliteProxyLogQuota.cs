using Microsoft.Data.Sqlite;
using Wafish.Storage;

namespace Wafish.Storage.Sqlite;

internal static class SqliteProxyLogQuota
{
    private const string MetaSize = "64 + coalesce(length(cast(Timestamp as blob)),0) + coalesce(length(cast(EventType as blob)),0) + coalesce(length(cast(Level as blob)),0) + coalesce(length(cast(RouteId as blob)),0) + coalesce(length(cast(ClusterId as blob)),0) + coalesce(length(cast(Method as blob)),0) + coalesce(length(cast(UpstreamPath as blob)),0) + coalesce(length(cast(TraceId as blob)),0) + coalesce(length(cast(DownstreamUrl as blob)),0)";
    private const string BodySize = "32 + coalesce(length(cast(Message as blob)),0) + coalesce(length(cast(RequestBody as blob)),0) + coalesce(length(cast(ResponseBody as blob)),0) + coalesce(length(cast(RequestHeaders as blob)),0) + coalesce(length(cast(ResponseHeaders as blob)),0) + coalesce(length(cast(DownstreamBody as blob)),0) + coalesce(length(cast(Exception as blob)),0)";

    public static async Task<ProxyLogQuotaResult> ApplyAsync(SqliteConnection connection, SqliteTransaction transaction,
        ProxyLogQuota quota, CancellationToken ct)
    {
        async Task<long> Scalar(string sql, long? limit = null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            if (limit.HasValue) command.Parameters.AddWithValue("@limit", limit.Value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        var beforeBodies = await Scalar("SELECT count(*) FROM proxy_logs_body");
        var beforeMeta = await Scalar("SELECT count(*) FROM proxy_logs_meta");
        if (quota.BodyBytes > 0)
            await Scalar($"DELETE FROM proxy_logs_body WHERE MetaId IN (SELECT MetaId FROM (SELECT MetaId, sum({BodySize}) OVER (ORDER BY MetaId DESC) AS charge FROM proxy_logs_body) WHERE charge > @limit); SELECT changes();", quota.BodyBytes);
        if (quota.MetadataBytes > 0)
            await Scalar($"DELETE FROM proxy_logs_meta WHERE Id IN (SELECT Id FROM (SELECT Id, sum({MetaSize}) OVER (ORDER BY Id DESC) AS charge FROM proxy_logs_meta) WHERE charge > @limit); SELECT changes();", quota.MetadataBytes);
        await Scalar("UPDATE proxy_logs_meta SET HasRequestBody=0, HasResponseBody=0 WHERE (HasRequestBody<>0 OR HasResponseBody<>0) AND NOT EXISTS (SELECT 1 FROM proxy_logs_body WHERE MetaId=proxy_logs_meta.Id); SELECT changes();");
        return new(await Scalar($"SELECT coalesce(sum({MetaSize}),0) FROM proxy_logs_meta"),
            await Scalar($"SELECT coalesce(sum({BodySize}),0) FROM proxy_logs_body"),
            beforeMeta - await Scalar("SELECT count(*) FROM proxy_logs_meta"),
            beforeBodies - await Scalar("SELECT count(*) FROM proxy_logs_body"));
    }
}
