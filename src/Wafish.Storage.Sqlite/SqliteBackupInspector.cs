using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Storage;

namespace Wafish.Storage.Sqlite;

/// <summary>Offline inspection results. Errors never contain credentials or database content.</summary>
public sealed record SqliteBackupReport(bool Valid, bool RestorationVerified, string Format, string? Error,
    IReadOnlyList<string> AppliedMigrations, IReadOnlyList<string> PendingMigrations,
    long? ConfigurationVersion = null, int? RouteCount = null, int? ClusterCount = null, int? DestinationCount = null);

/// <summary>Inspects standalone backups through private copies. The source file is never opened for writing.</summary>
public static class SqliteBackupInspector
{
    public static Task<SqliteBackupReport> InspectAsync(string path, string? password = null, CancellationToken cancellationToken = default)
        => InspectCoreAsync(path, password, false, cancellationToken);

    public static Task<SqliteBackupReport> VerifyRestoreAsync(string path, string? password = null, CancellationToken cancellationToken = default)
        => InspectCoreAsync(path, password, true, cancellationToken);

    private static async Task<SqliteBackupReport> InspectCoreAsync(string path, string? password, bool restore, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wafish-backup-check-" + Guid.NewGuid().ToString("N"));
        var format = "Unknown";
        string[] applied = [], pending = [];
        try
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path + "-wal") || File.Exists(path + "-journal"))
                return Failure("StandaloneBackupRequired");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var copy = Path.Combine(directory, "backup.db");
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            {
                var header = new byte[16];
                await source.ReadExactlyAsync(header, ct);
                format = Encoding.ASCII.GetString(header) == "SQLite format 3\0" ? "Plaintext" : "Encrypted";
                source.Position = 0;
                await using var destination = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await source.CopyToAsync(destination, ct);
            }
            var connectionString = new SqliteConnectionStringBuilder { DataSource = copy, Password = password ?? "", Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString();
            var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddWafishStorage();
            services.Configure<StorageOptions>(options => options.Sqlite.ConnectionString = connectionString);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<SqliteConnectionFactory>();
            await using (var connection = factory.CreateRawConnection())
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check(1)";
                if (!string.Equals(await command.ExecuteScalarAsync(ct) as string, "ok", StringComparison.Ordinal))
                    return Failure("IntegrityCheckFailed");
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('schema_migrations', 'yarp_routes', 'yarp_clusters')";
                var tables = new HashSet<string>(StringComparer.Ordinal);
                await using (var reader = await command.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
                if (tables.Count == 0) return Failure("NotGatewayDatabase");
                if (tables.Contains("schema_migrations"))
                {
                    command.CommandText = "SELECT id FROM schema_migrations ORDER BY id";
                    var ids = new List<string>();
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
                    applied = ids.ToArray();
                }
                var normalized = applied.Select(SqliteSchemaMigrator.NormalizeMigrationId).ToHashSet(StringComparer.Ordinal);
                pending = SqliteSchemaMigrator.MigrationIds.Except(normalized).ToArray();
                if (normalized.Except(SqliteSchemaMigrator.MigrationIds).Any()) return Failure("UnsupportedSchemaVersion");
            }
            if (!restore) return new(true, false, format, null, applied, pending);

            // Migrations and all subsequent reads operate only on the disposable copy.
            await new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance).RunMigrationAsync(ct);
            await using var restored = factory.CreateRawConnection();
            await restored.OpenAsync(ct);
            var snapshot = await SqliteConfigurationSnapshotRepository.ReadSnapshotAsync(restored, ct);
            var clusters = snapshot.Clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var route in snapshot.Routes)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(route.ClusterId) && !clusters.Contains(route.ClusterId)) return Failure("MissingClusterReference");
                ValidateJson(route.ConfigJson); ValidateJson(route.Metadata); ValidateJson(route.Transforms, JsonValueKind.Array);
            }
            foreach (var cluster in snapshot.Clusters) { ValidateJson(cluster.ConfigJson); ValidateJson(cluster.HealthCheckConfig); }
            foreach (var destination in snapshot.Destinations)
            {
                if (!clusters.Contains(destination.ClusterId)) return Failure("MissingClusterReference");
                ValidateJson(destination.Metadata);
            }
            return new(true, true, format, null, applied, pending, snapshot.Version,
                snapshot.Routes.Count, snapshot.Clusters.Count, snapshot.Destinations.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return Failure("InvalidConfigurationJson"); }
        catch (SqliteException) { return Failure("DatabaseUnreadableWithSuppliedKey"); }
        catch (IOException) { return Failure("SourceUnavailableOrTruncated"); }
        catch (UnauthorizedAccessException) { return Failure("SourceUnavailable"); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or InvalidOperationException or ArgumentException)
        { return Failure("InvalidSchemaOrConfiguration"); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        SqliteBackupReport Failure(string error) => new(false, false, format, error, applied, pending);
    }
    private static void ValidateJson(string? json, JsonValueKind expected = JsonValueKind.Object)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != expected)
            throw new JsonException("Configuration value has an invalid shape.");
    }
}
