using System.Text.Json;
using Wafish.Administration;
using Wafish.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class DashboardContractChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Administration: API contracts remain unchanged after removing Razor pages", async () =>
        {
            var expected = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "contracts", "dashboard-routes.json")))!;
            var actual = await GetRoutesAsync();
            if (!expected.SequenceEqual(actual))
            {
                throw new InvalidOperationException("Changed HTTP routes: " + string.Join("; ", expected.Except(actual).Concat(actual.Except(expected))));
            }
        });
        await run("Administration: registration is idempotent and does not enable Razor", async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddWafishStorage();
            builder.Services.AddWafishAdministration(options => options.Auth.JwtSecret = new string('b', 64));
            var registrations = builder.Services.Count;
            builder.Services.AddWafishAdministration();
            if (builder.Services.Count != registrations)
            {
                throw new InvalidOperationException("Administration services were registered twice.");
            }

            await using var app = builder.Build();
            if (app.Services.GetService<IRazorViewEngine>() != null)
            {
                throw new InvalidOperationException("Administration registered a Razor view engine.");
            }

            var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
            if (actions.OfType<ControllerActionDescriptor>().Any(action => action.ControllerTypeInfo.Assembly.GetName().Name == "Wafish.Dashboard"))
            {
                throw new InvalidOperationException("Administration discovered presentation controllers without explicit registration.");
            }
        });
    }

    public static async Task<string[]> GetRoutesAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddWafishStorage();
        builder.Services.AddWafishAdministration(options =>
        {
            options.RoutePrefix = "control";
            options.Auth.JwtSecret = new string('a', 64);
        });
        builder.Services.AddWafishAI();
        await using var app = builder.Build();
        return app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(action => string.Join(',', (action.ActionConstraints?.OfType<HttpMethodActionConstraint>()
                .SelectMany(constraint => constraint.HttpMethods) ?? []).Order()) + " " + action.AttributeRouteInfo?.Template)
            .Order(StringComparer.Ordinal).ToArray();
    }
}
