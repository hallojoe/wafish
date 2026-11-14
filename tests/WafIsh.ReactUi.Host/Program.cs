using WafIsh.Administration;
using WafIsh.ReactUi;
using WafIsh.Runtime;
using WafIsh.Storage.Sqlite;
using Microsoft.Data.Sqlite;

var password = Environment.GetEnvironmentVariable("UI_TEST_PASSWORD")
    ?? throw new InvalidOperationException("UI_TEST_PASSWORD is required for this test-only host.");
var directory = Path.Combine(Path.GetTempPath(), "aneiang-ui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls("http://127.0.0.1:" + (Environment.GetEnvironmentVariable("UI_TEST_PORT") ?? "5219"));
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Gateway:Storage:Sqlite:ConnectionString"] = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "gateway.db"), Password = password }.ToString(),
        ["Gateway:Dashboard:RoutePrefix"] = "aneiang",
        ["Gateway:Dashboard:Locale"] = "en-US",
        ["Gateway:Dashboard:Auth:AuthMode"] = Environment.GetEnvironmentVariable("UI_TEST_AUTH") ?? "DefaultJwt",
        ["Gateway:Dashboard:Auth:JwtPassword"] = password,
        ["Gateway:Dashboard:Auth:JwtUsername"] = "admin",
        ["Gateway:Dashboard:Auth:ApiKey"] = password,
        ["Gateway:Dashboard:Auth:JwtSecret"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        ["Gateway:Dashboard:Auth:TwoFactorStatePath"] = Path.Combine(directory, "twofactor.json"),
        ["AI:Enabled"] = "false",
        ["ReverseProxy:Clusters:demo:Destinations:local:Address"] = "http://127.0.0.1:9/",
        ["ReverseProxy:Routes:demo:ClusterId"] = "demo",
        ["ReverseProxy:Routes:demo:Match:Path"] = "/demo/{**catch-all}",
        ["ReverseProxy:Routes:demo:Metadata:retained-field"] = "preserve-me"
    });
    builder.Services.AddWafIshStorage();
    builder.Services.AddWafIshRuntime();
    builder.Services.AddWafIshReactUi();
    builder.Services.AddWafIshAI();
    await using var app = builder.Build();
    if (app.Services.GetService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>() != null || File.Exists(Path.Combine(AppContext.BaseDirectory, "WafIsh.Dashboard.dll")))
        throw new InvalidOperationException("React UI must run without Razor or the dashboard assembly.");
    app.UseWafIshRuntime();
    app.MapWafIshAdministration();
    app.MapWafIshReactUi();
    app.MapWafIshProxy();
    await app.RunAsync();
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}
