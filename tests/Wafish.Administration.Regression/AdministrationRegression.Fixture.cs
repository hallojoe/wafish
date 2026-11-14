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
    private async Task Check(string name, Func<Task> action)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { await action(); report.Record(name, started); Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { report.Record(name, started, ex); failures.Add(name + ": " + ex.Message); }
    }
    
    private async Task<WebApplication> StartHost(string mode, bool enableAi = false, string environment = "Development", bool allowAnonymousLoopback = false, bool enableMcp = false, int mcpPort = 8090, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null, Action<IServiceCollection>? configure = null, bool registration = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = path });
        builder.Logging.ClearProviders();
        if (certificate == null)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
        });
        }
    
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Storage:Sqlite:ConnectionString"] = new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(path, "admin.db"), Password = key }.ToString(),
            ["Gateway:Dashboard:RoutePrefix"] = "ops",
            ["Gateway:Dashboard:Locale"] = "fr-FR",
            ["Gateway:Security:ControlPlane:AllowAnonymousLoopback"] = allowAnonymousLoopback.ToString(),
            ["Gateway:Dashboard:Auth:AuthMode"] = mode,
            ["Gateway:Dashboard:Auth:ApiKey"] = key,
            ["Gateway:Dashboard:Auth:JwtPassword"] = key,
            ["Gateway:Dashboard:Auth:JwtUsername"] = "admin",
            ["Gateway:Dashboard:Auth:TwoFactorStatePath"] = Path.Combine(path, "twofactor.json"),
            ["Gateway:Dashboard:Auth:JwtSecret"] = new string('x', 64),
            ["AI:Enabled"] = "true",
            ["AI:McpServer:Enabled"] = "true",
            ["AI:McpServer:Port"] = mcpPort.ToString()
        });
        builder.Services.AddWafishStorage();
        builder.Services.AddWafishRuntime(enableRegistration: registration);
        builder.Services.AddWafishAdministration();
        builder.Services.AddWafishAdministration();
        if (enableAi)
        {
            builder.Services.AddWafishAI();
        }
    
        if (enableMcp)
        {
            builder.Services.AddWafishMcp();
        }
    
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapWafishAdministration();
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
    
    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
        Timeout = TimeSpan.FromSeconds(15)
    };
    
    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
    {
        using (response)
        {
            response.EnsureSuccessStatusCode();
            Require(response.Content.Headers.ContentType?.MediaType == "application/json", "Expected JSON response.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
    }
    
    private async Task VerifyHub(Uri baseAddress, string name, string? invocation, string expected)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-Key", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await socket.ConnectAsync(new UriBuilder(baseAddress) { Scheme = "ws", Path = "/ops/hubs/" + name }.Uri, timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        if (invocation != null)
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(invocation), WebSocketMessageType.Text, true, timeout.Token);
        }
    
        var received = new StringBuilder();
        var buffer = new byte[16384];
        while (!received.ToString().Contains(expected, StringComparison.Ordinal))
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Require(result.MessageType == WebSocketMessageType.Text, "Hub closed before the expected message.");
            received.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        Require(!received.ToString().Contains("\"error\"", StringComparison.Ordinal), "Hub returned an error.");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Check completed", timeout.Token);
    }
    
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
