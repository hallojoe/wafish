using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Services;

internal static class OperationPermissionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run,
        Func<Action<IServiceCollection>, Task<WebApplication>> start, string credential)
    {
        await run("Permissions: bound restrictions replace administrator defaults", () =>
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Auth:Permissions:0"] = "Read" }).Build();
            var options = config.Get<DashboardOptions>()!;
            Require(options.Auth.Permissions!.SequenceEqual(new[] { DashboardPermission.Read }), "Configuration binding retained administrator permissions.");
            return Task.CompletedTask;
        });
        await run("Permissions: readers cannot mutate configuration, credentials, plugins, or AI tools", async () =>
        {
            await using var host = await start(services => services.Configure<DashboardOptions>(options => options.Auth.Permissions = [DashboardPermission.Read]));
            using var client = Client(host); client.DefaultRequestHeaders.Add("X-Api-Key", credential);
            Require((await client.GetAsync("/ops/api/routes")).StatusCode == HttpStatusCode.OK, "Reader cannot inspect routes.");
            foreach (var path in new[] { "/ops/api/config/routes/batch-delete", "/ops/api/plugins/test/toggle", "/ops/api/ai/tool/execute", "/ops/api/ai/config", "/ops/api/2fa/verify", "/ops/api/config/changes", "/ops/api/plugins/preview" })
            {
                using var response = await client.PostAsJsonAsync(path, new { toolName = "create_route", arguments = "{}" });
                Require(response.StatusCode == HttpStatusCode.Forbidden, "Reader mutation was not denied: " + path + " " + response.StatusCode);
            }
            foreach (var path in new[] { "/ops/api/settings/database", "/ops/api/settings/database/backup", "/ops/api/ai/config", "/ops/api/2fa/setup", "/ops/api/webhook/settings" })
                Require((await client.GetAsync(path)).StatusCode == HttpStatusCode.Forbidden, "Reader accessed credential management: " + path);
            using var existingConnection = await SessionLifecycleChecks.Connect(client.BaseAddress!, "traffic", null, "X-Api-Key", credential);
            var options = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
            using (var hub = await client.PostAsync("/ops/hubs/traffic/negotiate?negotiateVersion=1", null))
                Require(hub.IsSuccessStatusCode, "Reader cannot subscribe.");
            var accessor = host.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = host.Services };
            context.Request.Scheme = "https";
            context.Request.Headers["X-Api-Key"] = credential;
            accessor.HttpContext = context;
            try
            {
                var tools = ActivatorUtilities.CreateInstance<Wafish.Dashboard.Modules.Dashboard.Services.AIFunctionService>(host.Services);
                Require(!(await tools.ExecuteReadToolAsync("list_plugins", null)).Contains("403"), "Read tool denied reader.");
                Require((await tools.ExecuteWriteToolAsync("create_route", null)).Contains("403"), "AI write tool bypassed permissions.");
                Require((await tools.ExecuteWriteToolAsync("toggle_plugin", null)).Contains("403"), "AI plugin tool bypassed permissions.");
                options.Permissions = [];
                Require((await tools.ExecuteReadToolAsync("list_plugins", null)).Contains("403"), "AI read tool ignored permissions.");
            }
            finally { accessor.HttpContext = null; }
            options.Permissions = [];
            using (var hub = await client.PostAsync("/ops/hubs/traffic/negotiate?negotiateVersion=1", null))
                Require(hub.StatusCode == HttpStatusCode.Forbidden, "Hub ignored read permission.");
            await SessionLifecycleChecks.Closed(existingConnection);
            var denied = host.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent(50).Where(a => a.Action == "PermissionDenied").ToArray();
            Require(denied.Length >= 5 && denied.All(a => a.Operator == "api-key"), "Denied operations omitted their actor.");
        });
        await run("Permissions: authenticated claims restrict defaults and custom operation rules remain supported", async () =>
        {
            await using var host = await start(services => services.Configure<DashboardOptions>(options =>
            {
                options.Auth.AuthorizeRequest = context =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader"), new Claim(DashboardPermissionService.PermissionClaim, "Read")], "fixture"));
                    return Task.FromResult(true);
                };
            }));
            using var client = Client(host);
            Require((await client.GetAsync("/ops/api/routes")).IsSuccessStatusCode, "Claims reader rejected.");
            Require((await client.PostAsJsonAsync("/ops/api/config/routes/batch-delete", new { })).StatusCode == HttpStatusCode.Forbidden, "Claims reader inherited administrator defaults.");
            var options = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
            options.AuthorizeOperation = (_, permission, _) => ValueTask.FromResult(permission == DashboardPermission.ConfigurationWrite);
            Require((await client.GetAsync("/ops/api/routes")).StatusCode == HttpStatusCode.Forbidden, "Custom permission rule ignored.");
            Require(host.Services.GetRequiredService<IConfigChangeAuditLog>().GetRecent().Any(a => a.Operator == "reader" && a.Action == "PermissionDenied"), "Custom actor missing.");
        });
    }
    private static HttpClient Client(WebApplication host) => new() { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
