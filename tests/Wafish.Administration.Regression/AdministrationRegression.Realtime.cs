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
    private async Task CheckApiKeyHostAsync()
    {
        await using (var host = await StartHost("ApiKey"))
        {
            using var client = Client(host);
            await CheckApiKeyAccessAsync(host, client);
            await CheckConfigurationAsync(host, client);
            await CheckBackupsAsync(host, client);
            await Check("Administration realtime hubs accept subscriptions and send overview updates", async () =>
            {
                await VerifyHub(client.BaseAddress!, "overview", null, "OverviewUpdate");
                host.Services.GetRequiredService<IProxyLogStore>().Add(new LogEntry { Timestamp = DateTime.Now, EventType = LogEventType.ProxyResponse, RouteId = "test-route", ClusterId = "test-cluster", StatusCode = 200, ElapsedMs = 1 });
                await VerifyHub(client.BaseAddress!, "traffic",
                    "{\"type\":1,\"invocationId\":\"1\",\"target\":\"SubscribeToRoutes\",\"arguments\":[[\"test-route\"]]}\u001e", "TrafficUpdate");
            });
            await host.StopAsync();
        }
    }

}
