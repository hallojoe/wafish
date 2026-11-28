using Wafish.Services;
using Microsoft.AspNetCore.Builder;
using Wafish.Runtime;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Dashboard.Infrastructure.HostedServices;
using Wafish.Dashboard.Infrastructure.Deployment;
using Wafish.Dashboard.Infrastructure.Realtime;
using Wafish.Dashboard.Modules.Dashboard.Controllers;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Dashboard.Modules.GatewayConfig.Services;
using Wafish.Plugin.ProxyLog;
using Wafish.Plugin.ProxyLog.Services;
using Wafish.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using DashboardInfrastructure = Wafish.Dashboard.Infrastructure;

namespace Wafish.Administration;

/// <summary>Administration registration without Razor presentation.</summary>
public static class AdministrationServiceCollectionExtensions
{
    /// <summary>
    /// Register administration services with configurable authentication and route prefix.
    /// Options are bound from <c>Gateway:Dashboard</c> configuration section.
    /// </summary>
    public static IServiceCollection AddWafishAdministration(
        this IServiceCollection services,
        Action<DashboardOptions>? configureOptions = null)
    {
        if (services.Any(d => d.ServiceType == typeof(AdministrationMarker)))
        {
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }

            return services;
        }
        services.AddSingleton<AdministrationMarker>();
        services.AddSingleton<Wafish.Dashboard.Infrastructure.Diagnostics.DiagnosticBundleService>();
        var features = new AdministrationFeatures();
        services.AddSingleton(features);
        services.Configure<MvcOptions>(options => options.Conventions.Add(new AdministrationFeatureConvention(features)));
        services.AddDashboardOptions(configureOptions);
        services.AddWafishRuntime();
        services.AddOptions<Wafish.Infrastructure.Middleware.GatewayMiddlewareOptions>()
            .Configure<Microsoft.Extensions.Options.IOptions<DashboardOptions>>((runtime, dashboard) =>
                runtime.RoutePrefix = dashboard.Value.RoutePrefix);
        services.AddDashboardWebInfrastructure();
        services.AddDashboardStorageAndAudit();
        services.AddDashboardSecurity();
        services.AddHostedService<AdministrationSecurityValidator>();
        services.AddDashboardProxyLog();
        services.AddDashboardQueryServices();
        services.AddDashboardRealtimeAndPerformance();
        services.AddDashboardConfigPersistence();
        services.AddDashboardWarmupServices();
        return services;
    }

    #region Option binding

    private static IServiceCollection AddDashboardOptions(
        this IServiceCollection services,
        Action<DashboardOptions>? configureOptions)
    {
        services.AddOptions<DashboardOptions>()
            .BindConfiguration(DashboardOptions.SectionName)
            .Configure<IConfiguration>((options, config) =>
            {
                var controlPlane = config.GetSection(ControlPlaneSecurityOptions.SectionName).Get<ControlPlaneSecurityOptions>();
                if (controlPlane == null || string.IsNullOrWhiteSpace(controlPlane.AuthMode))
                {
                    return;
                }

                // Unified configuration overrides legacy dashboard settings. The explicit callback runs last.
                if (!Enum.TryParse<DashboardAuthMode>(controlPlane.AuthMode, true, out var mode) || !Enum.IsDefined(mode))
                {
                    throw new OptionsValidationException(nameof(DashboardOptions), typeof(DashboardOptions), ["Unsupported control-plane authentication mode."]);
                }

                options.Auth.AuthMode = mode;
                options.Auth.JwtSecret = controlPlane.JwtSecret;

                if (string.Equals(controlPlane.AuthMode, "ApiKey", StringComparison.OrdinalIgnoreCase))
                {
                    options.Auth.AuthMode = DashboardAuthMode.ApiKey;
                    options.Auth.ApiKey = controlPlane.ApiKey;
                    options.Auth.ApiKeyHeaderName = string.IsNullOrWhiteSpace(controlPlane.ApiKeyHeaderName) ? "X-Api-Key" : controlPlane.ApiKeyHeaderName;
                }
                else if (string.Equals(controlPlane.AuthMode, "CustomJwt", StringComparison.OrdinalIgnoreCase))
                {
                    options.Auth.AuthMode = DashboardAuthMode.CustomJwt;
                    options.Auth.JwtUsername = controlPlane.Username ?? "admin";
                    options.Auth.JwtPassword = controlPlane.Password;
                }
                else if (mode is DashboardAuthMode.DefaultJwt or DashboardAuthMode.BasicAuth)
                {
                    options.Auth.JwtUsername = controlPlane.Username ?? "admin";
                    options.Auth.JwtPassword = controlPlane.Password;
                }
            });

        services.AddOptions<DashboardOptions>()
            .PostConfigure(options => options.Locale = "en-US")
            .Validate(options => Enum.IsDefined(options.Auth.AuthMode), "Unsupported dashboard authentication mode.")
            .Validate(options => options.Auth.AuthMode != DashboardAuthMode.ApiKey ||
                (!string.IsNullOrWhiteSpace(options.Auth.ApiKey) && !string.IsNullOrWhiteSpace(options.Auth.ApiKeyHeaderName)),
                "Dashboard API key authentication requires a nonempty key and header name.")
            .Validate(options => options.Auth.AuthMode is not (DashboardAuthMode.DefaultJwt or DashboardAuthMode.CustomJwt or DashboardAuthMode.BasicAuth) ||
                (!string.IsNullOrWhiteSpace(options.Auth.JwtPassword) && options.Auth.JwtPassword.Length >= Math.Max(6, options.Auth.MinPasswordLength)),
                "Password authentication requires a nonempty password that meets the configured minimum length.")
            .Validate(options => options.Auth.AuthMode is not (DashboardAuthMode.CustomJwt or DashboardAuthMode.BasicAuth) ||
                !string.IsNullOrWhiteSpace(options.Auth.JwtUsername), "Custom authentication requires a username.")
            .Validate(options => !options.Auth.EnableTwoFactor || !string.IsNullOrWhiteSpace(options.Auth.TwoFactorSecret),
                "Two-factor authentication requires a secret.")
            .Validate(options => options.Auth.JwtSecret == null || System.Text.Encoding.UTF8.GetByteCount(options.Auth.JwtSecret) >= 32,
                "JWT signing keys require at least 32 bytes.")
            .ValidateOnStart();

        // Sub-options: Auth and ProxyLog can be injected independently
        services.AddOptions<DashboardAuthOptions>()
            .BindConfiguration("Gateway:Dashboard:Auth");
        services.AddOptions<ProxyLogOptions>()
            .BindConfiguration("Gateway:Dashboard:ProxyLog");

        services.AddOptions<ConfigHistoryOptions>()
            .BindConfiguration(ConfigHistoryOptions.SectionName)
            .PostConfigure(options =>
            {
                options.MaxSnapshots = Math.Max(1, options.MaxSnapshots);
                options.SnapshotQueueCapacity = Math.Max(1, options.SnapshotQueueCapacity);
            });

        // Deployment options — BindConfiguration provides raw config; AddWafishAdministrationDeployment
        // (if called) will PostConfigure to normalize Mode (Auto→Split/AllInOne).
        services.AddOptions<DeploymentOptions>()
            .BindConfiguration(DeploymentOptions.SectionName);

        // AI assistant options — binds from top-level "AI" section
        services.AddOptions<AIOptions>()
            .BindConfiguration(AIOptions.SectionName);

        // Alert pipeline: gateway alerts flow into the notification dispatcher,
        // which fans out to subscribed webhook platforms with cooldown + retry.
        services.AddSingleton<DashboardInfrastructure.Notifications.NotificationDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<DashboardInfrastructure.Notifications.NotificationDispatcher>());
        services.AddSingleton<DashboardInfrastructure.Notifications.WebhookAlertService>();
        services.AddSingleton<Wafish.Dashboard.Infrastructure.Alert.IGatewayAlertService,
            Wafish.Dashboard.Infrastructure.Notifications.WebhookAlertService>();

        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        return services;
    }

    #endregion

    #region Web infrastructure (MVC, SignalR, compression)

    private static IServiceCollection AddDashboardWebInfrastructure(this IServiceCollection services)
    {
        // MVC controllers with JSON camelCase naming policy.
        // Comments and trailing commas are tolerated so route/cluster editors and config import
        // accept relaxed JSON (matching docs/yarp_all.json style).
        var mvc = services.AddMvcCore()
            .AddApplicationPart(typeof(DashboardAuthController).Assembly)
            .AddApplicationPart(typeof(Wafish.Plugins.CircuitBreaker.Controllers.CircuitBreakerController).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip;
                options.JsonSerializerOptions.AllowTrailingCommas = true;
            });

        // MVC discovers referenced assemblies by default. Presentation requires explicit registration.
        foreach (var part in mvc.PartManager.ApplicationParts.Where(part =>
            part.Name is "Wafish.Dashboard" or "Wafish.Dashboard.Views").ToArray())
        {
            mvc.PartManager.ApplicationParts.Remove(part);
        }

        services.AddMemoryCache();
        services.AddSignalR();

        return services;
    }

    #endregion

    #region Configuration audit

    private static IServiceCollection AddDashboardStorageAndAudit(this IServiceCollection services)
    {
        // Storage backend (e.g. AddWafishStorage) is registered by the host application.
        // Repository implementations are selected by the host.

        // DynamicYarpConfigService loads config from storage on StartAsync.
        // Schema migration is triggered lazily by the connection factory on first
        // connection use, so tables are guaranteed to exist regardless of registration order.
        services.AddSingleton<ConfigChangeEventDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigChangeEventDispatcher>());

        return services;
    }

    #endregion

    #region Security (Gateway API auth, JWT, MVC conventions)

    private static IServiceCollection AddDashboardSecurity(this IServiceCollection services)
    {
        Wafish.Extensions.WafishServiceCollectionExtensions.AddGatewayApiAuth(services);
        services.AddSingleton<GatewayApiAuthFilter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MvcOptions>, GatewayApiAuthMvcOptionsSetup>());

        services.TryAddSingleton<AuthenticationAttemptLimiter>();
        services.AddSingleton<IDashboardAuthorizationService, DashboardAuthorizationService>();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IDashboardPermissionService, DashboardPermissionService>();
        services.AddSingleton<DashboardSessionStore>();
        services.AddSingleton<DashboardWebAuthnService>();
        services.AddSingleton<IDashboardWebAuthnService>(sp => sp.GetRequiredService<DashboardWebAuthnService>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<DashboardWebAuthnService>());
        services.TryAddSingleton<JwtSecretProvider>();
        services.AddSingleton<IConfigureOptions<MvcOptions>, DashboardMvcOptionsSetup>();

        return services;
    }

    #endregion

    #region Proxy log store + persistence

    private static IServiceCollection AddDashboardProxyLog(this IServiceCollection services)
    {
        // Register ProxyLogPluginOptions mapped from DashboardOptions.ProxyLog
        services.AddSingleton<IConfigureOptions<ProxyLogPluginOptions>>(sp =>
        {
            return new ConfigureNamedOptions<ProxyLogPluginOptions>(Options.DefaultName, pluginOpts =>
            {
                var dashboardOpts = sp.GetRequiredService<IOptions<DashboardOptions>>().Value;
                var logOpts = dashboardOpts.ProxyLog;
                pluginOpts.LogPersistenceEnabled = logOpts.LogPersistenceEnabled;
                pluginOpts.LogMetaRetentionDays = logOpts.LogMetaRetentionDays;
                pluginOpts.LogBodyRetentionDays = logOpts.LogBodyRetentionDays;
                pluginOpts.EnableProxyRequestBodyCapture = logOpts.EnableProxyRequestBodyCapture;
                pluginOpts.EnableProxyResponseBodyCapture = logOpts.EnableProxyResponseBodyCapture;
                pluginOpts.LogMaxBodyLength = logOpts.LogMaxBodyLength;
                pluginOpts.LogMaxBodyBufferBytes = logOpts.LogMaxBodyBufferBytes;
                pluginOpts.EnableLogSampling = logOpts.EnableLogSampling;
                pluginOpts.LogSamplingRate = logOpts.LogSamplingRate;
                pluginOpts.LogErrorsOnly = logOpts.LogErrorsOnly;
                pluginOpts.MinLogLevel = logOpts.MinLogLevel;
                pluginOpts.LogBufferCapacity = logOpts.LogBufferCapacity;
                pluginOpts.LogHeaderBlacklist = logOpts.LogHeaderBlacklist ?? [];
                pluginOpts.LogQueryBlacklist = logOpts.LogQueryBlacklist ?? [];
                pluginOpts.LogJsonFieldSanitizeList = logOpts.LogJsonFieldSanitizeList ?? [];
            });
        });

        return services;
    }

    #endregion

    #region Query services + editable policy

    private static IServiceCollection AddDashboardQueryServices(this IServiceCollection services)
    {
        services.AddSingleton<IDashboardInfoQueryService, DashboardInfoQueryService>();
        services.TryAddSingleton<IOperatorViewService, OperatorViewService>();
        services.AddSingleton<IDashboardClusterQueryService, DashboardClusterQueryService>();
        services.AddSingleton<IDashboardRouteQueryService, DashboardRouteQueryService>();
        services.AddSingleton<IDashboardLogQueryService, DashboardLogQueryService>();

        return services;
    }

    #endregion

    #region Real-time, performance, statistics

    private static IServiceCollection AddDashboardRealtimeAndPerformance(this IServiceCollection services)
    {
        services.AddSingleton<TrafficBroadcastService>();
        services.AddHostedService<TrafficBroadcastService>();

        services.AddSingleton<OverviewBroadcastService>();
        services.AddHostedService<OverviewBroadcastService>();
        services.AddSingleton<DashboardInfrastructure.Realtime.IOverviewSnapshotProvider, DashboardInfrastructure.Realtime.OverviewSnapshotProvider>();

        // Webhook notifications (DingTalk / generic endpoints for config-change events)
        services.AddHttpClient("webhook");
        services.AddSingleton<DashboardInfrastructure.Notifications.WebhookNotificationService>();

        return services;
    }

    #endregion

    #region Config persistence + identity + health

    private static IServiceCollection AddDashboardConfigPersistence(this IServiceCollection services)
    {
        services.AddSingleton<ConfigPersistenceService>();
        services.AddSingleton<IConfigPersistenceService>(sp => sp.GetRequiredService<ConfigPersistenceService>());
        services.AddSingleton<IConfigDiffService, ConfigDiffService>();
        services.AddSingleton<ConfigSnapshotScheduler>();
        services.AddSingleton<IConfigSnapshotScheduler>(sp => sp.GetRequiredService<ConfigSnapshotScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<ConfigSnapshotScheduler>());
        services.AddSingleton<IGatewayIdentityService, GatewayIdentityService>();

        return services;
    }

    #endregion

    #region Warmup services

    private static IServiceCollection AddDashboardWarmupServices(this IServiceCollection services)
    {
        services.AddBackgroundHostedService<StartupWarmupService>();
        return services;
    }

    #endregion

    /// <summary>
    /// Register deployment-related services (EndpointRoleResolver, config validators, snapshot store, hot-reload).
    /// Configuration is resolved from DI, so this can be called as <c>services.AddWafishAdministrationDeployment()</c>.
    /// </summary>
    public static IServiceCollection AddWafishAdministrationDeployment(this IServiceCollection services)
    {
        services.AddOptions<DeploymentOptions>()
            .BindConfiguration(DeploymentOptions.SectionName);


        services.TryAddSingleton<DeploymentRestartState>();
        services.TryAddSingleton<EndpointRoleResolver>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = sp.GetRequiredService<IOptions<DeploymentOptions>>().Value;
            options.ResolvedEndpoints.Clear();
            return new EndpointRoleResolver(configuration, options);
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DeploymentConfigValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Wafish.Dashboard.Infrastructure.HostedServices.KestrelEndpointChangeDetector>());
        return services;
    }

    private sealed class AdministrationMarker;
}
