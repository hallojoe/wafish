using Wafish.Administration;
using Wafish.ReactUi;
using Wafish.Runtime;
using Wafish.Storage.Sqlite;
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
        ["Gateway:Dashboard:RoutePrefix"] = Environment.GetEnvironmentVariable("UI_TEST_API_PREFIX") ?? "aneiang",
        ["Gateway:ReactUi:RoutePrefix"] = Environment.GetEnvironmentVariable("UI_TEST_UI_PREFIX") ?? "console",
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
    builder.Services.AddWafishStorage();
    builder.Services.AddWafishRuntime();
    builder.Services.AddWafishReactUi();
    builder.Services.AddWafishAI();
    await using var app = builder.Build();
    if (app.Services.GetService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>() != null || File.Exists(Path.Combine(AppContext.BaseDirectory, "Wafish.Dashboard.dll")))
    {
        throw new InvalidOperationException("React UI must run without Razor or the dashboard assembly.");
    }

    var pathBase = Environment.GetEnvironmentVariable("UI_TEST_PATH_BASE");
    if (!string.IsNullOrEmpty(pathBase))
    {
        app.UsePathBase(pathBase);
    }

    app.UseWafishRuntime();
    app.MapWafishAdministration();
    app.MapWafishReactUi();
    app.MapWafishProxy();
    await app.RunAsync();
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}
