using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wafish.ReactUi.Host;
using Wafish.Services;
using Wafish.Storage;

internal static class UiFixtureChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("UI fixtures: options reject invalid credentials, modes, paths, ports, and profiles", OptionsAsync);
        await run("UI fixtures: all authentication modes isolate baseline, large, and plugin profiles", ProfilesAsync);
        await run("UI fixtures: HTTPS hosting and failed starts release their private directories", CleanupAsync);
        await run("UI fixtures: process-selected faults return real mutation revisions without control endpoints", FaultsAsync);
    }
    private static Task OptionsAsync()
    {
        var values = new Dictionary<string, string?> { ["UI_TEST_PASSWORD"] = Guid.NewGuid().ToString("N") };
        var defaults = UiTestFixtureOptions.FromEnvironment(key => values.GetValueOrDefault(key));
        Check(defaults.Profile == "baseline" && defaults.Port == 5219 && defaults.AuthMode == "DefaultJwt" && !defaults.Https, "Default fixture options changed.");
        foreach (var (key, value) in new[] { ("UI_TEST_PASSWORD", ""), ("UI_TEST_PORT", "-1"), ("UI_TEST_PORT", "65536"), ("UI_TEST_AUTH", "unknown"), ("UI_TEST_PROFILE", "unknown"), ("UI_TEST_PATH_BASE", "edge"), ("UI_TEST_PATH_BASE", "/edge/"), ("UI_TEST_API_PREFIX", ""), ("UI_TEST_UI_PREFIX", " ") })
        {
            var invalid = new Dictionary<string, string?>(values) { [key] = value };
            try { UiTestFixtureOptions.FromEnvironment(name => invalid.GetValueOrDefault(name)); throw new Exception("Invalid option accepted: " + key); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        Check(!defaults.ToString()!.Contains(defaults.Password), "Options exposed the password.");
        return Task.CompletedTask;
    }
    private static async Task ProfilesAsync()
    {
        var directories = new HashSet<string>();
        foreach (var mode in new[] { "DefaultJwt", "BasicAuth", "ApiKey" })
        foreach (var profile in new[] { "baseline", "large", "plugins" })
        {
            var fixture = await UiTestFixture.CreateAsync(new() { Password = Guid.NewGuid().ToString("N"), Port = 0, AuthMode = mode, Profile = profile });
            try
            {
                Check(directories.Add(fixture.DirectoryPath), "Fixtures shared a directory.");
                await fixture.StartAsync();
                var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
                Check(config.GetRoutes().Count == (profile == "large" ? 251 : 1), "Profile route count differs.");
                var bindings = await fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>().GetBindingsSnapshotAsync();
                Check(bindings.Bindings.Count == (profile == "plugins" ? 2 : 0), "Plugin profile leaked or failed to seed.");
                if (profile == "plugins") Check(bindings.Bindings.All(binding => binding.Enabled && binding.ScopeId == "demo") && bindings.Bindings.Select(binding => binding.PluginId).Order().SequenceEqual(new[] { "compression", "rate-limit" }), "Representative bindings were not applied.");
                using var client = new HttpClient { BaseAddress = new Uri(fixture.App.Urls.Single()) };
                Check((await client.GetAsync("/console")).StatusCode == HttpStatusCode.OK, "Fixture UI is unavailable.");
                Check((await client.GetAsync("/aneiang/api/routes")).StatusCode == HttpStatusCode.Unauthorized, "Fixture API bypassed authentication.");
                if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(fixture.DirectoryPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Fixture state permissions are not private.");
            }
            finally { await fixture.DisposeAsync(); await fixture.DisposeAsync(); }
            Check(!Directory.Exists(fixture.DirectoryPath), "Fixture retained its state after disposal.");
        }
    }
    private static async Task CleanupAsync()
    {
        var options = UiTestFixtureOptions.FromEnvironment(key => new Dictionary<string, string?>
        {
            ["UI_TEST_PASSWORD"] = Guid.NewGuid().ToString("N"), ["UI_TEST_PORT"] = "0", ["UI_TEST_HTTPS"] = "true",
            ["UI_TEST_PATH_BASE"] = "/edge", ["UI_TEST_API_PREFIX"] = "manage/api", ["UI_TEST_UI_PREFIX"] = "manage/ui", ["UI_TEST_ROOT"] = Path.GetTempPath()
        }.GetValueOrDefault(key));
        var secure = await UiTestFixture.CreateAsync(options);
        try
        {
            await secure.StartAsync();
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Subject == "CN=localhost" };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(secure.App.Urls.Single()) };
            Check((await client.GetAsync("/edge/manage/ui")).IsSuccessStatusCode, "HTTPS nested UI failed.");
            Check(!Directory.EnumerateFiles(secure.DirectoryPath, "*.pfx").Any(), "Certificate was written to disk.");
        }
        finally { await secure.DisposeAsync(); }
        Check(!Directory.Exists(secure.DirectoryPath), "HTTPS state was retained.");

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var occupied = await UiTestFixture.CreateAsync(new() { Password = Guid.NewGuid().ToString("N"), Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        try
        {
            try { await occupied.StartAsync(); throw new Exception("Occupied port started."); }
            catch (IOException) { }
        }
        finally { await occupied.DisposeAsync(); }
        Check(!Directory.Exists(occupied.DirectoryPath), "Failed startup retained fixture state.");
    }
    private static async Task FaultsAsync()
    {
        foreach (var (profile, outcome, status) in new[] { ("storage-failure", "StorageFailed", 503), ("version-conflict", "VersionConflict", 409), ("delayed-failure", "StorageFailed", 503), ("activation-failure", "ActivationFailed", 500) })
        {
            var password = Guid.NewGuid().ToString("N");
            await using var fixture = await UiTestFixture.CreateAsync(new() { Password = password, Port = 0, Profile = profile, AuthMode = "ApiKey" });
            await fixture.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(fixture.App.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Api-Key", password);
            using var response = await client.PutAsJsonAsync("/aneiang/api/config/routes/fixture-draft", new { ClusterId = "demo", Match = new { Path = "/fixture-draft" } });
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = body.RootElement;
            Check((int)response.StatusCode == status && result.GetProperty("outcome").GetString() == outcome, "Fault profile did not produce its real mutation outcome: " + profile);
            var persisted = result.GetProperty("persistedVersion").GetInt64();
            var active = result.GetProperty("activeVersion").GetInt64();
            Check(profile != "activation-failure" || persisted > active, "Activation fault lost the committed revision.");
            Check(!fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>().GetRoutes().Any(route => route.RouteId == "fixture-draft"), "A failed mutation became active.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
