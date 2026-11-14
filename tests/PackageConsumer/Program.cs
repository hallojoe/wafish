using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using WafIsh.Runtime;
using WafIsh.Storage.Sqlite;
using WafIsh.Storage;
using WafIsh.Services;
#if ADMIN || UI
using WafIsh.Administration;
#endif
#if UI
using WafIsh.ReactUi;
#endif

var directory = Path.Combine(Path.GetTempPath(), "wafish-consumer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var password = Guid.NewGuid().ToString("N");
try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
        ["Gateway:Storage:Sqlite:ConnectionString"] = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "gateway.db"), Password = password }.ToString(),
        ["Gateway:Dashboard:Auth:AuthMode"] = "ApiKey", ["Gateway:Dashboard:Auth:ApiKey"] = password,
        ["Gateway:Dashboard:RoutePrefix"] = "manage" });
    builder.Services.AddWafIshStorage().AddWafIshRuntime();
#if UI
    builder.Services.AddWafIshReactUi();
#elif ADMIN
    builder.Services.AddWafIshAdministration();
#endif
    await using var app = builder.Build();
    app.Urls.Add("http://127.0.0.1:0");
    app.UseWafIshRuntime();
    app.MapWafIshProxy();
#if ADMIN || UI
    app.MapWafIshAdministration();
#endif
#if UI
    app.MapWafIshReactUi();
#endif
    await app.StartAsync();
    try
    {
        var config = app.Services.GetRequiredService<IDynamicYarpConfigService>();
        if (config.GetRoutes().Count != 0) throw new Exception("Unexpected routes");
        var plugins = app.Services.GetRequiredService<WafIsh.Plugins.IGatewayPluginManager>();
        if (!plugins.GetAllManifests().Any(m => m.Id == "response-cache")) throw new Exception("Plugin composition failed");
        var factory = app.Services.GetRequiredService<SqliteConnectionFactory>();
        await using var connection = factory.CreateConnection(); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "SELECT sqlite3mc_version()";
        if (string.IsNullOrEmpty(Convert.ToString(await query.ExecuteScalarAsync()))) throw new Exception("Native encryption provider missing");
        await using (var reopened = new SqliteConnection(new SqliteConnectionStringBuilder(connection.ConnectionString) { Pooling = false }.ToString()))
        {
            await reopened.OpenAsync();
            await using var read = reopened.CreateCommand(); read.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            if (Convert.ToInt64(await read.ExecuteScalarAsync()) == 0) throw new Exception("Encrypted database did not reopen");
        }
        var rejected = false;
        try
        {
            await using var wrongKey = new SqliteConnection(new SqliteConnectionStringBuilder(connection.ConnectionString) { Password = "wrong-key", Pooling = false }.ToString());
            await wrongKey.OpenAsync();
            await using var read = wrongKey.CreateCommand(); read.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            await read.ExecuteScalarAsync();
        }
        catch (SqliteException) { rejected = true; }
        if (!rejected) throw new Exception("Encrypted database accepted a wrong key");
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
#if UI
        var html = await client.GetStringAsync("/console");
        var asset = System.Text.RegularExpressions.Regex.Match(html, "src=\"\\./(assets/[^\"]+)\"").Groups[1].Value;
        if (asset.Length == 0 || !(await client.GetAsync("/console/" + asset)).IsSuccessStatusCode) throw new Exception("Embedded asset missing");
#endif
#if ADMIN || UI
        using var denied = await client.GetAsync("/manage/api/routes");
        if (denied.StatusCode != System.Net.HttpStatusCode.Unauthorized) throw new Exception("Administration authentication failed");
        client.DefaultRequestHeaders.Add("X-Api-Key", password);
        using var allowed = await client.GetAsync("/manage/api/routes");
        if (!allowed.IsSuccessStatusCode) throw new Exception("Administration API unavailable: " + allowed.StatusCode);
#else
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "WafIsh.Administration" or "WafIsh.ReactUi")) throw new Exception("Runtime loaded presentation packages");
#endif
        Console.WriteLine("PASS: isolated package host, plugin composition, native encryption, and selected endpoints");
    }
    finally { await app.StopAsync(); }
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
