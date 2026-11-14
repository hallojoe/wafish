using Wafish.Runtime;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Plugin.ProxyLog.Services;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Wafish.Administration;
using Wafish.Dashboard.Infrastructure.I18n;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed partial class AdministrationRegressionSuite
{
    private async Task CheckBackupsAsync(WebApplication host, HttpClient client)
    {
            await Check("Administration serves system settings and database downloads without Razor", async () =>
            {
                using var system = await ReadJson(await client.GetAsync("/ops/api/settings/system"));
                Require(system.RootElement.GetProperty("data").GetProperty("routePrefix").GetString() == "ops", "Settings prefix changed.");
                Require(system.RootElement.GetProperty("data").GetProperty("locale").GetString() == "en-US", "Unsupported configured locale did not fall back to English.");
                foreach (var url in new[] { "/ops/api/settings/database", "/ops/api/settings/database/backup" })
                {
                    using var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    Require(response.Content.Headers.ContentType?.MediaType == "application/octet-stream", "Database response changed.");
                    Require((await response.Content.ReadAsByteArrayAsync()).Length > 4096, "Database download was empty.");
                }
            });
            await Check("Encrypted database backup retains committed WAL data and rejects missing keys", async () =>
            {
                await using var source = host.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnection();
                await source.OpenAsync();
                await using var command = source.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE backup_probe (value TEXT); INSERT INTO backup_probe VALUES ('committed-in-wal');";
                await command.ExecuteNonQueryAsync();
                using var response = await client.GetAsync("/ops/api/settings/database/backup");
                response.EnsureSuccessStatusCode();
                Require(response.Content.Headers.ContentType?.MediaType == "application/octet-stream", "Backup was not a database.");
                var backupPath = Path.Combine(path, "downloaded-backup.db");
                await File.WriteAllBytesAsync(backupPath, await response.Content.ReadAsByteArrayAsync());
                var backupSettings = new SqliteConnectionStringBuilder(source.ConnectionString)
                {
                    DataSource = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = backupPath }.Uri.AbsoluteUri + "?cipher=sqlcipher&legacy=4",
                    Pooling = false
                };
                await using (var backup = new SqliteConnection(backupSettings.ToString()))
                {
                    await backup.OpenAsync();
                    await using var read = backup.CreateCommand();
                    read.CommandText = "SELECT value FROM backup_probe";
                    Require(await read.ExecuteScalarAsync() as string == "committed-in-wal", "Backup omitted a committed WAL write.");
                    read.CommandText = "PRAGMA integrity_check";
                    Require(await read.ExecuteScalarAsync() as string == "ok", "Backup failed integrity validation.");
                }
                backupSettings.Password = "";
                await using var noKey = new SqliteConnection(backupSettings.ToString());
                var rejected = false;
                try
                {
                    await noKey.OpenAsync();
                    await using var read = noKey.CreateCommand();
                    read.CommandText = "SELECT count(*) FROM backup_probe";
                    await read.ExecuteScalarAsync();
                }
                catch (SqliteException e) when (e.SqliteErrorCode == 26) { rejected = true; }
                Require(rejected, "Encrypted backup was readable without its key.");
            });
    }

}
