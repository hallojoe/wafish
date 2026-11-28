using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Dashboard.Infrastructure;

internal static class AuthorizationMatrixChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run,
        Func<Action<IServiceCollection>, Task<WebApplication>> start, string credential)
    {
        await run("Authorization matrix: every mapped action rejects missing and invalid credentials across four modes", async () =>
        {
            var clock = new MatrixClock();
            await using var host = await start(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.Configure<MvcOptions>(options => options.Filters.Add(new ProbeFilter()));
            });
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
            };
            var descriptors = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>().ToArray();
            var actions = descriptors.SelectMany(action => action.ActionConstraints!.OfType<HttpMethodActionConstraint>()
                .SelectMany(constraint => constraint.HttpMethods.Select(method => (Action: action, Method: method, Route: action.AttributeRouteInfo!.Template!)))).ToArray();
            var expected = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/dashboard-routes.json")))!;
            var actual = actions.Select(a => a.Method + " " + a.Route.Replace("ops/", "control/", StringComparison.Ordinal)).Order().ToArray();
            Require(actual.SequenceEqual(expected.Order()), "Mapped controller inventory differs. Missing: " + string.Join(", ", expected.Except(actual))
                + "; unexpected: " + string.Join(", ", actual.Except(expected)));
            var options = host.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Auth;
            foreach (var mode in new[] { DashboardAuthMode.ApiKey, DashboardAuthMode.BasicAuth, DashboardAuthMode.DefaultJwt, DashboardAuthMode.CustomJwt })
            {
                options.AuthMode = mode;
                string? token = null;
                if (mode is DashboardAuthMode.DefaultJwt or DashboardAuthMode.CustomJwt)
                {
                    using var login = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password = credential });
                    using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
                    token = body.RootElement.GetProperty("token").GetString();
                }
                foreach (var action in actions)
                {
                    var isEntry = (action.Action.ControllerTypeInfo == typeof(Wafish.Dashboard.Modules.Dashboard.Controllers.DashboardAuthController)
                        && action.Action.ActionName is "Login" or "Logout")
                        || (action.Action.ControllerTypeInfo == typeof(Wafish.Dashboard.Modules.Dashboard.Controllers.DashboardWebAuthnController)
                        && action.Action.ActionName is "BeginLogin" or "CompleteLogin");
                    foreach (var state in new[] { "anonymous", "invalid", "authorized" })
                    {
                        clock.Advance();
                        var path = "/" + Regex.Replace(action.Route, "\\{[^}]+\\}", "matrix");
                        using var request = new HttpRequestMessage(new HttpMethod(action.Method), path);
                        request.Headers.Add("X-Test-Authorization-Probe", "true");
                        if (state != "anonymous")
                        {
                            var value = state == "authorized" ? credential : "invalid-credential";
                            if (mode == DashboardAuthMode.ApiKey) request.Headers.Add("X-Api-Key", value);
                            else if (mode == DashboardAuthMode.BasicAuth) request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + value)));
                            else request.Headers.Authorization = new("Bearer", state == "authorized" ? token : value);
                        }
                        using var response = await client.SendAsync(request);
                        var expectedStatus = isEntry || state == "authorized" ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized;
                        Require(response.StatusCode == expectedStatus,
                            $"{mode} {state} {action.Method} {action.Route}: expected {expectedStatus}, received {response.StatusCode}.");
                    }
                }
            }
            await host.StopAsync();
        });
    }
    // Authorization filters execute before resource filters. No action or model binding runs for a probe.
    private sealed class ProbeFilter : IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (context.HttpContext.Request.Headers["X-Test-Authorization-Probe"] == "true") context.Result = new NoContentResult();
        }
        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
    private sealed class MatrixClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.TicksPerMinute;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
