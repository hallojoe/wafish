using Wafish.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class SqliteEncryptionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Storage: maintained native engine meets the patched version floor", async () =>
        {
            await using var provider = CreateProvider("Data Source=:memory:;Pooling=false");
            // Open a raw connection after factory initialization; migrations require persistent storage.
            _ = provider.GetRequiredService<SqliteConnectionFactory>();
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var sqlite = await Scalar(connection, "SELECT sqlite_version()");
            var cipher = await Scalar(connection, "SELECT sqlite3mc_version()");
            if (!Version.TryParse(sqlite, out var sqliteVersion) || sqliteVersion < new Version(3, 54, 1) ||
                !Version.TryParse(cipher?.Replace("SQLite3 Multiple Ciphers ", "", StringComparison.Ordinal), out var cipherVersion) || cipherVersion < new Version(2, 7, 0))
            {
                throw new InvalidOperationException("The loaded native database engine is below the reviewed security floor.");
            }

            Console.WriteLine($"Native storage: SQLite3MC {cipher}; SQLite {sqlite}");
        });

        foreach (var encrypted in new[] { false, true })
        {
            await run($"Storage: legacy {(encrypted ? "SQLCipher 4" : "plain SQLite")} data survives migration, write, and reopen", async () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "aneiang-legacy # ü;" + Guid.NewGuid().ToString("N") + ".db");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "storage", encrypted ? "sqlcipher4.db" : "plain.db"), path);
                var settings = new SqliteConnectionStringBuilder
                {
                    DataSource = path, Password = encrypted ? "compatibility-test-key" : "", Pooling = false
                };
                try
                {
                    await using (var provider = CreateProvider(settings.ToString()))
                    {
                        await using var connection = await provider.GetRequiredService<SqliteConnectionFactory>().CreateConnectionAsync();
                        await connection.OpenAsync();
                        if (await Scalar(connection, "SELECT value FROM compatibility_probe") != "legacy-data-retained")
                        {
                            throw new InvalidOperationException("Legacy data was lost during migration.");
                        }

                        await Scalar(connection, "INSERT INTO compatibility_probe VALUES ('new-data-retained'); SELECT count(*) FROM yarp_routes");
                        if (await Scalar(connection, "PRAGMA integrity_check") != "ok")
                        {
                            throw new InvalidOperationException("Migrated database failed its integrity check.");
                        }
                    }
                    await using var reopened = CreateProvider(settings.ToString());
                    await using var read = await reopened.GetRequiredService<SqliteConnectionFactory>().CreateConnectionAsync();
                    await read.OpenAsync();
                    if (await Scalar(read, "SELECT count(*) FROM compatibility_probe") != "2")
                    {
                        throw new InvalidOperationException("Legacy database did not retain the new write.");
                    }

                    var header = (await File.ReadAllBytesAsync(path)).Take(16).ToArray();
                    if (System.Text.Encoding.ASCII.GetString(header).StartsWith("SQLite format 3") == encrypted)
                    {
                        throw new InvalidOperationException("The migration changed database encryption unexpectedly.");
                    }
                }
                finally { DeleteDatabase(path); }
            });
        }

        await run("Storage: explicit SQLCipher URI options and pooling settings are preserved", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "aneiang-uri-" + Guid.NewGuid().ToString("N") + ".db");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "storage", "sqlcipher4.db"), path);
            try
            {
                var source = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = path }.Uri.AbsoluteUri;
                var settings = new SqliteConnectionStringBuilder
                {
                    DataSource = source + "?cipher=sqlcipher&legacy=4&mode=rw", Password = "compatibility-test-key", Pooling = false
                };
                await using var provider = CreateProvider(settings.ToString());
                await using var connection = await provider.GetRequiredService<SqliteConnectionFactory>().CreateConnectionAsync();
                var actual = new SqliteConnectionStringBuilder(connection.ConnectionString);
                if (actual.DataSource != settings.DataSource || actual.Pooling)
                {
                    throw new InvalidOperationException("Explicit connection settings changed.");
                }

                await connection.OpenAsync();
                if (await Scalar(connection, "SELECT value FROM compatibility_probe") != "legacy-data-retained")
                {
                    throw new InvalidOperationException("Explicit SQLCipher options did not open legacy data.");
                }
            }
            finally { DeleteDatabase(path); }
        });

        await run("Storage: encrypted database survives reopen and rejects missing or incorrect keys", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "aneiang-encrypted-" + Guid.NewGuid().ToString("N") + ".db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Password = Guid.NewGuid().ToString("N"),
                Pooling = false
            };
            try
            {
                // Use the production factory, including schema migration and provider selection.
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Storage:Sqlite:ConnectionString"] = connectionString.ToString()
                }).Build();
                var services = new ServiceCollection();
                services.AddSingleton<IConfiguration>(config);
                services.AddLogging();
                services.AddWafIshStorage();
                await using (var provider = services.BuildServiceProvider())
                {
                    await using var connection = await provider.GetRequiredService<SqliteConnectionFactory>().CreateConnectionAsync();
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT sqlite3mc_version()";
                    var cipherVersion = await command.ExecuteScalarAsync() as string;
                    if (string.IsNullOrWhiteSpace(cipherVersion))
                    {
                        throw new InvalidOperationException("The maintained encryption provider did not load.");
                    }

                    command.CommandText = "SELECT sqlite_version()";
                    Console.WriteLine($"Encrypted storage: SQLite3MC {cipherVersion}; SQLite {await command.ExecuteScalarAsync()}");
                    // Reuse the actual URI configuration for invalid-key checks below.
                    connectionString.ConnectionString = connection.ConnectionString;
                    command.CommandText = "CREATE TABLE migration_probe (value TEXT NOT NULL); INSERT INTO migration_probe VALUES ('retained');";
                    await command.ExecuteNonQueryAsync();
                }

                await using (var provider = services.BuildServiceProvider())
                {
                    await using var connection = await provider.GetRequiredService<SqliteConnectionFactory>().CreateConnectionAsync();
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT value FROM migration_probe";
                    if (await command.ExecuteScalarAsync() as string != "retained")
                    {
                        throw new InvalidOperationException("Encrypted data was lost after reopening the database.");
                    }
                }

                foreach (var invalidPassword in new[] { "", "incorrect-key" })
                {
                    var invalid = new SqliteConnectionStringBuilder(connectionString.ToString()) { Password = invalidPassword };
                    await using var connection = new SqliteConnection(invalid.ToString());
                    try
                    {
                        await connection.OpenAsync();
                        await using var command = connection.CreateCommand();
                        command.CommandText = "SELECT value FROM migration_probe";
                        await command.ExecuteScalarAsync();
                        throw new InvalidOperationException("Encrypted data was readable without the correct key.");
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode == 26)
                    {
                        // SQLITE_NOTADB is expected when the encryption key is absent or incorrect.
                    }
                }
            }
            finally
            {
                DeleteDatabase(path);
            }
        });
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Storage:Sqlite:ConnectionString"] = connectionString
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddWafIshStorage();
        return services.BuildServiceProvider();
    }

    private static async Task<string?> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(path + suffix);
        }
    }
}
