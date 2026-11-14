using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Services;
using Wafish.Storage;

internal static class NeedSixEndpointChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<Action<IServiceCollection>?, Task<WebApplication>> start, string key)
    {
        await run("Change set API: preview requires read access and apply requires configuration and plugin permissions", async () =>
        {
            await using var host = await start(null);
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            var state = host.Services.GetRequiredService<IDynamicYarpConfigService>().GetDynamicConfig()!;
            var bindings = await host.Services.GetRequiredService<IPluginConfigurationRepository>().GetBindingsSnapshotAsync();
            var request = new ConfigurationChangeSetRequest(new(state.Version, state.Routes.Select(r => r.Config).ToArray(), state.Clusters.Select(c => c.Config).ToArray()), bindings.Revision, bindings.Bindings);
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets/preview", request)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous preview succeeded.");
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
            client.DefaultRequestHeaders.Add("Idempotency-Key", "combined-api");
            var auth = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
            auth.Permissions = [DashboardPermission.Read];
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets/preview", request)).IsSuccessStatusCode, "Reader preview failed.");
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.Forbidden, "Reader applied changes.");
            auth.Permissions = [DashboardPermission.ConfigurationWrite];
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.Forbidden, "Configuration permission bypassed plugin permission.");
            auth.Permissions = [DashboardPermission.PluginManagement];
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).StatusCode == HttpStatusCode.Forbidden, "Plugin permission bypassed configuration permission.");
            auth.Permissions = [DashboardPermission.ConfigurationWrite, DashboardPermission.PluginManagement];
            using var response = await client.PostAsJsonAsync("/ops/api/config/change-sets", request);
            Check(response.IsSuccessStatusCode, "Authorized combined apply failed: " + await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<ConfigurationChangeSetResult>();
            Check(result!.Configuration.Success && !string.IsNullOrWhiteSpace(result.Actor), "Apply omitted its actor or outcome.");
            Check((await client.PostAsJsonAsync("/ops/api/config/change-sets", request)).IsSuccessStatusCode, "Retry lost the receipt.");
        });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
