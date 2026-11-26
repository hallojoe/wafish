using Wafish.Administration;
using Wafish.ReactUi;
using Wafish.Runtime;
using Wafish.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Storage.Entities;
using Wafish.Storage;
using Wafish.Services;

namespace Wafish.ReactUi.Host;

internal sealed class UiTestFixture : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _disposal;
    private readonly RSA _key;
    private readonly X509Certificate2 _certificate;
    private readonly UiTestFixtureOptions _settings;
    public WebApplication App { get; }
    public string DirectoryPath { get; }

    private UiTestFixture(WebApplication app, string directory, RSA key, X509Certificate2 certificate, UiTestFixtureOptions settings)
        => (App, DirectoryPath, _key, _certificate, _settings) = (app, directory, key, certificate, settings);

    public static async Task<UiTestFixture> CreateAsync(UiTestFixtureOptions settings, string[]? args = null)
    {
        settings.Validate();
        var directory = Path.Combine(settings.Root ?? Path.GetTempPath(), "wafish-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WebApplication? app = null;
        RSA? key = null;
        X509Certificate2? certificate = null;
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(UiTestFixture).Assembly.FullName });
            key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, settings.Port, listen =>
            {
                if (settings.Https) listen.UseHttps(certificate);
            }));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Storage:Sqlite:ConnectionString"] = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "gateway.db"), Password = settings.Password, Pooling = false }.ToString(),
                ["Gateway:Dashboard:RoutePrefix"] = settings.ApiPrefix,
                ["Gateway:ReactUi:RoutePrefix"] = settings.UiPrefix,
                ["Gateway:Dashboard:Locale"] = "en-US",
                ["Gateway:Dashboard:Auth:AuthMode"] = settings.AuthMode,
                ["Gateway:Dashboard:Auth:JwtPassword"] = settings.Password,
                ["Gateway:Dashboard:Auth:JwtUsername"] = "admin",
                ["Gateway:Dashboard:Auth:ApiKey"] = settings.Password,
                ["Gateway:Dashboard:Auth:JwtSecret"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                ["Gateway:Dashboard:Auth:Passkeys:Enabled"] = (settings.Profile == "passkeys").ToString(),
                ["Gateway:Dashboard:Auth:Passkeys:RelyingPartyId"] = "localhost",
                ["Gateway:Dashboard:Auth:Passkeys:Origin"] = "https://localhost:" + settings.Port,
                ["Gateway:Dashboard:Auth:Passkeys:StatePath"] = Path.Combine(directory, "passkeys.json"),
                ["Gateway:Dashboard:Auth:TwoFactorStatePath"] = Path.Combine(directory, "twofactor.json"),
                ["AI:Enabled"] = "false",
                ["ReverseProxy:Clusters:demo:Destinations:local:Address"] = "http://127.0.0.1:9/",
                ["ReverseProxy:Routes:demo:ClusterId"] = "demo",
                ["ReverseProxy:Routes:demo:Match:Path"] = "/demo/{**catch-all}",
                ["ReverseProxy:Routes:demo:Metadata:retained-field"] = "preserve-me"
            });
            if (settings.Profile == "large")
            {
                builder.Configuration.AddInMemoryCollection(Enumerable.Range(0, 250).SelectMany(index => new Dictionary<string, string?>
                {
                    [$"ReverseProxy:Routes:profile-{index:D3}:ClusterId"] = "demo",
                    [$"ReverseProxy:Routes:profile-{index:D3}:Match:Path"] = $"/profile/{index}/{{**catch-all}}"
                }));
            }
            builder.Services.AddWafishStorage();
            builder.Services.AddWafishRuntime();
            builder.Services.AddWafishReactUi();
            builder.Services.AddWafishAI();
            builder.Services.AddSingleton(new UiTestFaults(settings.Profile));
            builder.Services.AddSingleton<SqliteConfigurationSnapshotRepository>();
            builder.Services.AddSingleton<IConfigurationSnapshotRepository, UiTestSnapshotRepository>();
            builder.Services.AddSingleton<IGatewaySnapshotPublisher, UiTestSnapshotPublisher>();
            app = builder.Build();
            if (app.Services.GetService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>() != null || File.Exists(Path.Combine(AppContext.BaseDirectory, "Wafish.Dashboard.dll")))
            {
                throw new InvalidOperationException("React UI must run without Razor or the dashboard assembly.");
            }

            var pathBase = settings.PathBase;
            if (!string.IsNullOrEmpty(pathBase))
            {
                app.UsePathBase(pathBase);
            }

            app.UseWafishRuntime();
            app.MapWafishAdministration();
            app.MapWafishReactUi();
            app.MapWafishProxy();
            return new(app, directory, key, certificate, settings);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            certificate?.Dispose(); key?.Dispose();
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await App.StartAsync(cancellationToken);
        if (_settings.Profile == "plugins")
        {
            var bindings = App.Services.GetRequiredService<IPluginBindingMutationService>();
            foreach (var plugin in new[] { "rate-limit", "compression" })
                await bindings.UpsertAsync(new PluginBindingEntity
                {
                    Id = "profile-" + plugin, PluginId = plugin, PluginVersion = "1.0",
                    Scope = PluginBindingScope.Route, ScopeId = "demo", Enabled = true,
                    ConfigJson = plugin == "rate-limit" ? "{\"enabled\":true,\"permitLimit\":25,\"window\":\"1m\"}" : "{\"enabled\":true}"
                }, cancellationToken);
        }
        App.Services.GetRequiredService<UiTestFaults>().Armed = true;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try { await App.StopAsync(); }
        finally
        {
            try { await App.DisposeAsync(); }
            finally
            {
                _certificate.Dispose(); _key.Dispose();
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
