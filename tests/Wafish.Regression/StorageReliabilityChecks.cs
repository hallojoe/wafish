using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Storage;
using Wafish.Storage.Sqlite;
using Wafish.Storage.Sqlite.Migrations;

internal static class StorageReliabilityChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await StorageConformanceChecks.RunAsync(run, () => new StorageReliabilityFixture());
        await run("SQLite recovery: failed migration body, ledger write, and caller cancellation roll back before reopening", async () =>
        {
            foreach (var fault in new[] { "body", "ledger", "cancellation" })
            {
                await using var fixture = new StorageReliabilityFixture();
                var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
                await Initialize(factory);
                using var cancellation = new CancellationTokenSource();
                var migration = new ProbeMigration(1001, async (connection, transaction, ct) =>
                {
                    await Execute(connection, transaction, "CREATE TABLE migration_probe(value INTEGER); INSERT INTO migration_probe VALUES (1)", ct);
                    if (fault == "body") throw new InvalidOperationException("Injected migration body failure.");
                    if (fault == "ledger") await Execute(connection, transaction,
                        "CREATE TEMP TRIGGER reject_probe BEFORE INSERT ON schema_migrations WHEN NEW.id = 'probe_1001' BEGIN SELECT RAISE(ABORT, 'injected ledger failure'); END", ct);
                    if (fault == "cancellation") cancellation.Cancel();
                });
                var failed = false;
                try { await Migrator(factory, migration).RunMigrationAsync(cancellation.Token); }
                catch (Exception ex) when (ex is InvalidOperationException or SqliteException or OperationCanceledException) { failed = true; }
                Check(failed, "Injected fault was ignored.");
                await using var reopened = new StorageReliabilityFixture(fixture.Path);
                var next = reopened.Provider.GetRequiredService<SqliteConnectionFactory>();
                await using var inspect = await next.CreateConnectionAsync(); await inspect.OpenAsync();
                Check(await Scalar(inspect, "SELECT count(*) FROM sqlite_master WHERE name='migration_probe'") == 0, "Partial migration table survived rollback.");
                Check(await Scalar(inspect, "SELECT count(*) FROM schema_migrations WHERE id='probe_1001'") == 0, "Failed migration was marked complete.");
                await Migrator(next, new ProbeMigration(1001, (connection, transaction, ct) => Execute(connection, transaction,
                    "CREATE TABLE migration_probe(value INTEGER); INSERT INTO migration_probe VALUES (1)", ct))).RunMigrationAsync();
                Check(await Scalar(inspect, "SELECT count(*) FROM migration_probe") == 1, "Reopen did not complete the failed migration.");
            }
        });
        await run("SQLite recovery: process interruption preserves committed WAL and replays only the unfinished migration", async () =>
        {
            await using var fixture = new StorageReliabilityFixture();
            var ready = fixture.Path + ".ready";
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(StorageReliabilityChecks).Assembly.Location);
            start.ArgumentList.Add("--migration-crash"); start.ArgumentList.Add(fixture.Path); start.ArgumentList.Add(ready);
            start.Environment.Remove("WAFISH_TEST_REPORT");
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!File.Exists(ready))
                {
                    if (child.HasExited) throw new Exception("Migration worker exited early: " + await stderr);
                    await Task.Delay(10, timeout.Token);
                }
                Check(File.Exists(fixture.Path + "-wal") && new FileInfo(fixture.Path + "-wal").Length > 32, "Worker did not retain a committed WAL.");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync(timeout.Token);
                var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
                await using var connection = await factory.CreateConnectionAsync(); await connection.OpenAsync();
                Check(await Scalar(connection, "SELECT count(*) FROM migration_probe") == 1, "Crash lost committed data or exposed uncommitted data.");
                var skip = new ProbeMigration(1001, (_, _, _) => throw new Exception("Committed migration replayed."));
                var finish = new ProbeMigration(1002, (conn, tx, ct) => Execute(conn, tx, "INSERT INTO migration_probe VALUES (2)", ct));
                await Migrator(factory, skip, finish).RunMigrationAsync();
                Check(await Scalar(connection, "SELECT count(*) FROM migration_probe") == 2, "Unfinished migration did not recover.");
                Check(await Scalar(connection, "SELECT count(*) FROM schema_migrations WHERE id LIKE 'probe_%'") == 2, "Migration ledger diverged after recovery.");
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                await stdout; await stderr; File.Delete(ready);
            }
        });
        await run("SQLite logs: 50,000-row filtered queries remain bounded in memory and execution time", async () =>
        {
            await using var fixture = new StorageReliabilityFixture();
            var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
            await using (var connection = await factory.CreateConnectionAsync())
            {
                await connection.OpenAsync();
                await Execute(connection, null, """
                    WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<50000)
                    INSERT INTO proxy_logs_meta(Timestamp, EventType, Level, RouteId, StatusCode, UpstreamPath)
                    SELECT '2026-11-14T12:00:00.0000000Z', 'ProxyResponse', 'Information', 'stress', 200, '/stress/' || n FROM seq
                    """, default);
            }
            var logs = fixture.Provider.GetRequiredService<IProxyLogRepository>();
            await logs.SearchAsync(1, 1); // Initialize column metadata before measuring the query.
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var elapsed = Stopwatch.StartNew();
            var result = await logs.SearchAsync(1, int.MaxValue, routeId: "stress", keyword: "/stress/");
            var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            Check(result.TotalCount == 50000 && result.Items.Count == 500, "Stress query ignored bounds.");
            Check(bytes < 64 * 1024 * 1024 && elapsed.Elapsed < TimeSpan.FromSeconds(10), "Stress query exceeded its documented budget.");
            Console.WriteLine($"SQLite query budget: {bytes} allocated bytes; {elapsed.Elapsed.TotalMilliseconds:F1} ms; 50,000 rows.");
        });
        foreach (var encrypted in new[] { false, true })
        {
            await run($"SQLite backup: {(encrypted ? "encrypted" : "plaintext")} inspection and restore verification preserve source bytes", async () =>
            {
                var password = encrypted ? Guid.NewGuid().ToString("N") : null;
                await using var fixture = new StorageReliabilityFixture(password: password);
                var snapshots = fixture.Provider.GetRequiredService<IConfigurationSnapshotRepository>();
                await snapshots.SaveAsync(new(1, [new() { RouteId = "r", RouteUid = "r-uid", ClusterId = "c", ClusterUid = "c-uid", MatchPath = "/r", ConfigJson = "{}" }],
                    [new() { ClusterId = "c", ClusterUid = "c-uid", ConfigJson = "{}" }], [new() { ClusterId = "c", DestinationId = "d", Address = "http://localhost:1/" }]), 0);
                var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
                await using (var connection = await factory.CreateConnectionAsync())
                {
                    await connection.OpenAsync();
                    await Execute(connection, null, "DELETE FROM schema_migrations WHERE id IN ('020_proxy_log_utc_index','021_utc_timestamps')", default);
                }
                var before = await Hash(fixture.Path);
                var inspect = await SqliteBackupInspector.InspectAsync(fixture.Path, password);
                var verify = await SqliteBackupInspector.VerifyRestoreAsync(fixture.Path, password);
                Check(inspect.Valid && !inspect.RestorationVerified && inspect.PendingMigrations.Count > 0, "Offline inspection omitted pending migrations.");
                Check(verify.Valid && verify.RestorationVerified && verify.ConfigurationVersion == 1 && verify.RouteCount == 1 && verify.ClusterCount == 1 && verify.DestinationCount == 1,
                    "Restore verifier rejected a valid configuration: " + verify.Error);
                Check(verify.Format == (encrypted ? "Encrypted" : "Plaintext") && before == await Hash(fixture.Path), "Inspection changed the source or misreported encryption.");
                foreach (var operation in new[] { "inspect", "verify" })
                {
                    var command = await ToolAsync([operation, fixture.Path], password);
                    Check(command.ExitCode == 0 && command.Output.Contains("\"Valid\": true"), "Offline command failed: " + command.Output);
                }
                Check(before == await Hash(fixture.Path), "Offline command changed the backup.");
                if (encrypted)
                {
                    var wrong = await SqliteBackupInspector.VerifyRestoreAsync(fixture.Path, "incorrect-key");
                    Check(!wrong.Valid && !System.Text.Json.JsonSerializer.Serialize(wrong).Contains(password!), "Wrong-key failure leaked a key.");
                    Check(before == await Hash(fixture.Path), "Wrong-key verification changed source bytes.");
                    var command = await ToolAsync(["verify", fixture.Path], "incorrect-key");
                    Check(command.ExitCode == 1 && !command.Output.Contains(password!), "Offline command accepted an incorrect key or exposed credentials.");
                }
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { await SqliteBackupInspector.VerifyRestoreAsync(fixture.Path, password, cancellation.Token); throw new Exception("Expected cancellation."); }
                catch (OperationCanceledException) { }
            });
        }
        await run("SQLite backup: truncated, foreign, future-schema, and invalid-JSON backups fail without modifying their source", async () =>
        {
            await using var fixture = new StorageReliabilityFixture();
            var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
            await Initialize(factory);
            foreach (var fault in new[] { "future", "json", "jsonshape", "foreign", "wal", "truncated", "missing" })
            {
                var copy = fixture.Path + "." + fault;
                File.Copy(fixture.Path, copy);
                try
                {
                    if (fault == "foreign") File.Delete(copy);
                    if (fault is "future" or "json" or "jsonshape" or "foreign")
                    {
                        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
                        await connection.OpenAsync();
                        var sql = fault switch
                        {
                            "foreign" => "CREATE TABLE unrelated(value INTEGER)",
                            "future" => "INSERT INTO schema_migrations(id, description, applied_at) VALUES('999_future', 'future', '2026-11-14')",
                            "jsonshape" => "INSERT INTO yarp_clusters(cluster_id, created_at, updated_at, config_json) VALUES('broken', '2026-11-14', '2026-11-14', '[]')",
                            _ => "INSERT INTO yarp_clusters(cluster_id, created_at, updated_at, config_json) VALUES('broken', '2026-11-14', '2026-11-14', '{')"
                        };
                        await Execute(connection, null, sql, default);
                    }
                    if (fault == "wal") await File.WriteAllTextAsync(copy + "-wal", "active");
                    if (fault == "truncated") { using var stream = File.OpenWrite(copy); stream.SetLength(7); }
                    if (fault == "missing") File.Delete(copy);
                    var hash = File.Exists(copy) ? await Hash(copy) : null;
                    var result = await SqliteBackupInspector.VerifyRestoreAsync(copy);
                    Check(!result.Valid, "Invalid backup passed verification: " + fault);
                    if (hash != null) Check(hash == await Hash(copy), "Failed verification changed source: " + fault);
                }
                finally { foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(copy + suffix); }
            }
        });
        await run("SQLite backup command: missing arguments return a safe usage error", async () =>
        {
            var result = await ToolAsync(["verify"]);
            Check(result.ExitCode == 2 && result.Output.Contains("Usage:"), "Invalid arguments did not produce a usage error.");
        });
    }
    public static async Task CrashWorkerAsync(string path, string ready)
    {
        await using var fixture = new StorageReliabilityFixture(path);
        var factory = fixture.Provider.GetRequiredService<SqliteConnectionFactory>();
        await Initialize(factory);
        await using (var connection = factory.CreateRawConnection())
        {
            await connection.OpenAsync(); await Execute(connection, null, "PRAGMA journal_mode=WAL", default);
        }
        var committed = new ProbeMigration(1001, (conn, tx, ct) => Execute(conn, tx, "CREATE TABLE migration_probe(value INTEGER); INSERT INTO migration_probe VALUES(1)", ct));
        var interrupted = new ProbeMigration(1002, async (conn, tx, ct) =>
        {
            await Execute(conn, tx, "INSERT INTO migration_probe VALUES(2)", ct);
            await File.WriteAllTextAsync(ready, "ready", ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await Migrator(factory, committed, interrupted).RunMigrationAsync();
    }
    private static SqliteSchemaMigrator Migrator(SqliteConnectionFactory factory, params ISchemaMigration[] migrations)
        => new(factory, NullLogger<SqliteSchemaMigrator>.Instance, migrations);
    private static async Task Initialize(SqliteConnectionFactory factory) { await using var connection = await factory.CreateConnectionAsync(); await connection.OpenAsync(); }
    private static async Task Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct)
    { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; await command.ExecuteNonQueryAsync(ct); }
    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    { await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
    private static async Task<string> Hash(string path) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
    private static async Task<(int ExitCode, string Output)> ToolAsync(string[] arguments, string? password = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Wafish.slnx"))) root = root.Parent ?? throw new DirectoryNotFoundException();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tools/Wafish.Storage.Tool/bin", configuration, "net10.0/Wafish.Storage.Tool.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("WAFISH_BACKUP_PASSWORD");
        if (password != null) start.Environment["WAFISH_BACKUP_PASSWORD"] = password;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await output + await error);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class ProbeMigration(int version, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> apply) : ISchemaMigration
    {
        public int Version => version;
        public string Id => "probe_" + version;
        public string Description => "Synthetic regression migration";
        public Task UpAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct) => apply(connection, transaction, ct);
    }
}
