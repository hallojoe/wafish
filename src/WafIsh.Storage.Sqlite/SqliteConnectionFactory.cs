using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WafIsh.Storage;
using Microsoft.AspNetCore.WebUtilities;

namespace WafIsh.Storage.Sqlite;

/// <summary>
/// Shared SQLite connection factory used by all storage repositories.
/// Ensures SQLitePCL provider is initialized once and connection pooling is enabled.
/// Password-protected file connections retain the SQLCipher 4 format through SQLite3MC URI settings.
/// Schema migrations configure WAL journal mode.
/// Schema migration is triggered lazily on the first <see cref="CreateConnection"/> call
/// to guarantee tables exist before any repository reads the database.
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    internal TimeProvider Clock => _serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
    private readonly string _connectionString;
    private readonly IServiceProvider _serviceProvider;
    private static bool _providerSet;
    private static readonly object _providerLock = new();
    private readonly Lazy<Task> _migrationTask;

    public SqliteConnectionFactory(IOptions<StorageOptions> options, IServiceProvider serviceProvider)
    {
        _connectionString = NormalizeConnectionString(options.Value.Sqlite.ConnectionString);
        _serviceProvider = serviceProvider;
        EnsureProvider();

        // Lazy<Task> ensures migration runs exactly once on first connection use.
        // SqliteSchemaMigrator is resolved on-demand to avoid circular DI
        // (migrator depends on SqliteConnectionFactory for connections).
        _migrationTask = new Lazy<Task>(() =>
        {
            var migrator = ActivatorUtilities.CreateInstance<SqliteSchemaMigrator>(
                _serviceProvider, this);
            return migrator.RunMigrationAsync();
        });
    }

    /// <summary>
    /// Creates a new pooled SQLite connection. The first call triggers schema migration
    /// if it has not yet completed, ensuring tables exist before any repository access.
    /// </summary>
    public async ValueTask<SqliteConnection> CreateConnectionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _migrationTask.Value.WaitAsync(ct).ConfigureAwait(false);
        return new SqliteConnection(_connectionString);
    }

    /// <summary>
    /// Creates a new pooled SQLite connection (synchronous wrapper).
    /// The first call blocks until schema migration completes.
    /// </summary>
    public SqliteConnection CreateConnection()
    {
        // Block on the migration task; subsequent calls return immediately.
        _migrationTask.Value.GetAwaiter().GetResult();
        return new SqliteConnection(_connectionString);
    }

    /// <summary>
    /// Creates a raw connection bypassing migration guard.
    /// Used internally by <see cref="SqliteSchemaMigrator"/> to avoid deadlock
    /// when running migrations.
    /// </summary>
    internal SqliteConnection CreateRawConnection() => new(_connectionString);

    // Explicit interface implementations — return DbConnection for storage-agnostic callers
    DbConnection IDbConnectionFactory.CreateConnection() => CreateConnection();
    async ValueTask<DbConnection> IDbConnectionFactory.CreateConnectionAsync(CancellationToken ct)
        => await CreateConnectionAsync(ct).ConfigureAwait(false);

    private static void EnsureProvider()
    {
        if (_providerSet) return;
        lock (_providerLock)
        {
            if (_providerSet) return;
            SQLitePCL.Batteries_V2.Init();
            _providerSet = true;
        }
    }

    private static string NormalizeConnectionString(string cs)
    {
        var builder = new SqliteConnectionStringBuilder(string.IsNullOrWhiteSpace(cs)
            ? "Data Source=gateway-store.db" : cs);
        if (!string.IsNullOrEmpty(builder.Password) && !string.IsNullOrEmpty(builder.DataSource) &&
            builder.DataSource != ":memory:" && builder.Mode != SqliteOpenMode.Memory)
        {
            // SQLite3MC defaults to another cipher. Keep the existing SQLCipher 4 file format.
            // URI parameters select the cipher before Microsoft.Data.Sqlite supplies the key.
            var source = builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? builder.DataSource
                : new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = Path.GetFullPath(builder.DataSource) }.Uri.AbsoluteUri;
            var queryIndex = source.IndexOf('?');
            var query = QueryHelpers.ParseQuery(queryIndex < 0 ? "" : source[queryIndex..]);
            if (!query.TryGetValue("cipher", out var cipher))
            {
                source = QueryHelpers.AddQueryString(source, "cipher", "sqlcipher");
                cipher = "sqlcipher";
            }
            if (string.Equals(cipher, "sqlcipher", StringComparison.OrdinalIgnoreCase) && !query.ContainsKey("legacy"))
                source = QueryHelpers.AddQueryString(source, "legacy", "4");
            builder.DataSource = source;
        }
        return builder.ToString();
    }
}
