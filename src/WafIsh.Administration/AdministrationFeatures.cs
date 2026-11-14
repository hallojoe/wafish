using WafIsh.Dashboard.Infrastructure.Alert;
using WafIsh.Dashboard.Infrastructure.Notifications;
using WafIsh.Dashboard.Modules.Dashboard.Controllers;
using WafIsh.Dashboard.Modules.Dashboard.Services;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace WafIsh.Administration;

public sealed class AdministrationFeatures
{
    public bool Ai { get; internal set; }
    public bool Mcp { get; internal set; }
}

internal sealed class AdministrationFeatureConvention(AdministrationFeatures features) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        if (!features.Ai)
            foreach (var controller in application.Controllers.Where(c => c.ControllerType.AsType() == typeof(AIController)).ToArray())
                application.Controllers.Remove(controller);
    }
}

public static class AdministrationFeatureExtensions
{
    public static IServiceCollection AddWafIshAI(this IServiceCollection services)
    {
        var features = GetFeatures(services);
        if (features.Ai) return services;
        features.Ai = true;
        services.TryAddSingleton<AIConfigStore>();
        services.TryAddSingleton<AIFunctionService>();
        services.TryAddSingleton<AIService>();
        services.Replace(ServiceDescriptor.Singleton<IGatewayAlertService>(sp => new AIEnhancedAlertService(
            sp.GetRequiredService<WebhookAlertService>(), sp.GetRequiredService<AIConfigStore>(), sp,
            sp.GetRequiredService<ILogger<AIEnhancedAlertService>>())));
        return services;
    }

    public static IServiceCollection AddWafIshMcp(this IServiceCollection services)
    {
        var features = GetFeatures(services);
        if (features.Mcp) return services;
        features.Mcp = true;
        services.TryAddSingleton<AIConfigStore>();
        services.TryAddSingleton<AIFunctionService>();
        services.AddHostedService<McpServerHost>();
        return services;
    }

    private static AdministrationFeatures GetFeatures(IServiceCollection services)
    {
        services.AddWafIshAdministration();
        return (AdministrationFeatures)services.Single(d => d.ServiceType == typeof(AdministrationFeatures)).ImplementationInstance!;
    }
}
