using Microsoft.Data.Sqlite;
namespace WafIsh.Storage.Sqlite.Migrations;

/// <summary>Preserves timestamp precision while normalizing legacy text to UTC.</summary>
internal sealed class Migration021_UtcTimestamps : ISchemaMigration
{
    public int Version => 21;
    public string Id => "021_utc_timestamps";
    public string Description => "Normalize stored timestamp columns to UTC";
    private static readonly HashSet<string> TimeColumns = new(StringComparer.OrdinalIgnoreCase)
        { "timestamp", "createdat", "updatedat", "lastheartbeat", "installedat", "created_at", "updated_at", "applied_at" };
    public async Task UpAsync(SqliteConnection conn, SqliteTransaction transaction, CancellationToken ct)
    {
        var tables = new List<string>();
        await using (var query = conn.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }
        foreach (var table in tables)
        {
            var keys = new List<string>(); var times = new List<string>();
            await using (var query = conn.CreateCommand())
            {
                query.Transaction = transaction; query.CommandText = $"PRAGMA table_info({Quote(table)})";
                await using var reader = await query.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(1);
                    if (reader.GetInt32(5) > 0) keys.Add(name);
                    if (TimeColumns.Contains(name)) times.Add(name);
                }
            }
            if (keys.Count == 0) continue;
            foreach (var column in times)
            {
                // Paging bounds memory, including WITHOUT ROWID tables.
                var offset = 0;
                while (true)
                {
                    var rows = new List<object[]>();
                    await using (var query = conn.CreateCommand())
                    {
                        query.Transaction = transaction;
                        query.CommandText = $"SELECT {string.Join(",", keys.Select(Quote))},{Quote(column)} FROM {Quote(table)} ORDER BY {string.Join(",", keys.Select(Quote))} LIMIT 256 OFFSET @offset";
                        query.Parameters.AddWithValue("@offset", offset);
                        await using var reader = await query.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct)) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
                    }
                    foreach (var row in rows)
                    {
                        if (row[^1] is not string value) continue;
                        var normalized = StorageTimestamp.Normalize(value);
                        if (normalized == value) continue;
                        await using var update = conn.CreateCommand(); update.Transaction = transaction;
                        update.CommandText = $"UPDATE {Quote(table)} SET {Quote(column)} = @value WHERE " + string.Join(" AND ", keys.Select((k, i) => $"{Quote(k)} IS @k{i}"));
                        update.Parameters.AddWithValue("@value", normalized);
                        for (var i = 0; i < keys.Count; i++) update.Parameters.AddWithValue($"@k{i}", row[i]);
                        await update.ExecuteNonQueryAsync(ct);
                    }
                    if (rows.Count < 256) break;
                    offset += rows.Count;
                }
            }
        }
    }
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
