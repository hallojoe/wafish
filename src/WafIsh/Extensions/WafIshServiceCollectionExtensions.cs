using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Controllers;
using Wafish.Models;
using Wafish.Services;
using Wafish.Services.ProxyConfigHealth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;

namespace Wafish.Extensions;

/// <summary>
/// WafIsh service registration extensions.
/// </summary>
public static class WafIshServiceCollectionExtensions
{
    // -- Gateway

    /// <summary>
    /// Register the YARP gateway with full pipeline support.
    /// Automatically loads routes/clusters from <c>ReverseProxy</c> config section and enables dynamic config updates.
    /// </summary>
    /// <param name="services">IServiceCollection</param>
    /// <param name="configureReverseProxy">Optional YARP pipeline customization (transforms, service discovery, etc.).</param>
    /// <param name="enableRegistration">
    /// Whether to expose the route registration API (<c>GatewayConfigController</c>).
    /// Set to <c>false</c> when this gateway should not accept external route registration requests.
    /// Default: <c>true</c>.
    /// </param>
    public static IServiceCollection AddWafIsh(
        this IServiceCollection services,
        Action<IReverseProxyBuilder>? configureReverseProxy = null,
        bool enableRegistration = true)
    {
        // Guard: prevent double registration
        if (services.Any(sd => sd.ServiceType == typeof(WafIshProxyConfigProvider)))
        {
            return services;
        }

        services.AddGatewayApiAuth();
        services.TryAddSingleton(new GatewayControlPlaneOptions { EnableRegistration = enableRegistration });
        services.TryAddSingleton<GatewayApiAuthFilter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MvcOptions>, GatewayApiAuthMvcOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GatewayControlPlaneSecurityValidator>());

        var proxyBuilder = services.AddReverseProxy();

        // Enable YARP active health checks (periodic probing of backend destinations)
        // YARP registers IActiveHealthCheckMonitor internally; just need health check services
        services.AddHealthChecks();

        // Register custom load balancing policies
        services.AddSingleton<ILoadBalancingPolicy, IpBasedLoadBalancingPolicy>();

        configureReverseProxy?.Invoke(proxyBuilder);

        // WafIshProxyConfigProvider: single source of truth (static config from IConfiguration +
        // dynamic config loaded later by DynamicYarpConfigService). Replaces InMemoryConfigProvider.
        services.AddSingleton<WafIshProxyConfigProvider>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var section = config.GetSection("ReverseProxy");
            return new WafIshProxyConfigProvider(
                YarpConfigParser.ParseRoutes(section.GetSection("Routes")),
                YarpConfigParser.ParseClusters(section.GetSection("Clusters")));
        });

        // Sole config provider - both static + dynamic
        services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<WafIshProxyConfigProvider>());

        // Dynamic config service (depends on IRouteRepository, IClusterRepository and IConfigChangeAuditLog
        // which are registered by Dashboard when AddWafIshAdministration is called)
        services.AddSingleton<DynamicYarpConfigService>();
        services.AddSingleton<IDynamicYarpConfigService>(sp => sp.GetRequiredService<DynamicYarpConfigService>());
        services.AddSingleton<IGatewaySnapshotCompiler, GatewaySnapshotCompiler>();
        services.AddSingleton<IGatewaySnapshotPublisher, GatewaySnapshotPublisher>();

        // Proxy config apply-error capture: singleton store + YARP IConfigChangeListener.
        // YARP collects all IConfigChangeListener registrations via IEnumerable<T>, so
        // registering this singleton is enough to receive reload failure callbacks.
        services.AddSingleton<IProxyConfigErrorStore, ProxyConfigErrorStore>();
        services.AddSingleton<IConfigChangeListener, YarpConfigErrorListener>();

        // Built-in transform options
        services.AddOptions<BuiltinTransformOptions>()
            .BindConfiguration(BuiltinTransformOptions.SectionName);

        // Register protocol controllers. Razor presentation is registered by the dashboard package.
        services.AddControllers()
            .AddApplicationPart(typeof(GatewayConfigController).Assembly);

        services.AddGrpc(options =>
        {
            // Register gRPC auth interceptor (Phase 2: endpoint authentication)
            options.Interceptors.Add<GrpcAuthInterceptor>();
        });
        services.AddSingleton<GrpcAuthInterceptor>();

        // Remove registration API endpoints when disabled (security: no route = 404, not 401/403)
        if (!enableRegistration)
        {
            services.AddSingleton<IConfigureOptions<MvcOptions>, DisableRegistrationApiMvcOptionsSetup>();
        }

        // Registration client (gateway can itself register with an upstream gateway)
        services.AddWafIshClientInternal();

        return services;
    }

    // -- Gateway API authorization

    /// <summary>
    /// Enable authorization for the gateway config API (register-route, delete-route, etc.).
    /// Supports BasicAuth and ApiKey modes.
    /// </summary>
    public static IServiceCollection AddGatewayApiAuth(
        this IServiceCollection services,
        Action<GatewayApiAuthOptions>? configureOptions = null)
    {
        services.TryAddSingleton<JwtSecretProvider>();
        services.TryAddSingleton<ControlPlaneAccessPolicy>();
        services.AddOptions<ControlPlaneSecurityOptions>()
            .BindConfiguration(ControlPlaneSecurityOptions.SectionName);

        services.AddOptions<GatewayApiAuthOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                // Precedence: explicit callback, unified settings, legacy settings, dashboard fallback.
                var unified = config.GetSection(ControlPlaneSecurityOptions.SectionName).Get<ControlPlaneSecurityOptions>();
                if (unified?.AuthMode != null)
                {
                    options.Mode = ParseAuthMode(unified.AuthMode);
                    options.Username = string.Equals(unified.AuthMode, "DefaultJwt", StringComparison.OrdinalIgnoreCase)
                        ? "admin" : unified.Username;
                    options.Password = unified.Password;
                    options.ApiKey = unified.ApiKey;
                    options.JwtSecret = unified.JwtSecret;
                    options.ApiKeyHeaderName = unified.ApiKeyHeaderName;
                    options.AllowApiKeyInQuery = unified.AllowApiKeyInQuery;
                    return;
                }

                var legacy = config.GetSection(GatewayApiAuthOptions.SectionName);
                if (legacy.Exists())
                {
                    legacy.Bind(options);
                    return;
                }

                var dashboard = config.GetSection("Gateway:Dashboard:Auth");
                var mode = dashboard["AuthMode"];
                if (mode == null)
                {
                    return;
                }

                options.Mode = ParseAuthMode(mode);
                options.Username = string.Equals(mode, "CustomJwt", StringComparison.OrdinalIgnoreCase)
                    ? dashboard["JwtUsername"] : "admin";
                options.Password = dashboard["JwtPassword"];
                options.ApiKey = dashboard["ApiKey"];
                options.JwtSecret = dashboard["JwtSecret"];
                options.ApiKeyHeaderName = dashboard["ApiKeyHeaderName"] ?? "X-Api-Key";
            })
            .Configure(configureOptions ?? (_ => { }))
            .Configure<IServiceProvider>((options, provider) =>
            {
                if (options.Mode == GatewayApiAuthMode.Jwt && string.IsNullOrWhiteSpace(options.JwtSecret))
                {
                    options.JwtSecret = provider.GetRequiredService<JwtSecretProvider>().GetSecret(options.JwtSecret);
                }
            })
            .Validate(options => Enum.IsDefined(options.Mode), "Unsupported gateway authentication mode.")
            .Validate(options => options.Mode != GatewayApiAuthMode.ApiKey ||
                (!string.IsNullOrWhiteSpace(options.ApiKey) && !string.IsNullOrWhiteSpace(options.ApiKeyHeaderName)),
                "API key authentication requires a nonempty key and header name.")
            .Validate(options => options.Mode != GatewayApiAuthMode.BasicAuth ||
                (!string.IsNullOrWhiteSpace(options.Username) && !string.IsNullOrWhiteSpace(options.Password)),
                "Basic authentication requires a nonempty username and password.")
            .Validate(options => !options.AllowApiKeyInQuery, "API keys in URLs are not supported. Use the configured header.")
            .Validate(options => options.Mode != GatewayApiAuthMode.Jwt || System.Text.Encoding.UTF8.GetByteCount(options.JwtSecret ?? "") >= 32,
                "JWT signing keys require at least 32 bytes.")
            .ValidateOnStart();

        return services;
    }

    private static GatewayApiAuthMode ParseAuthMode(string value)
    {
        if (string.Equals(value, "DefaultJwt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "CustomJwt", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayApiAuthMode.Jwt;
        }

        if (Enum.TryParse<GatewayApiAuthMode>(value, true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        throw new OptionsValidationException(nameof(GatewayApiAuthOptions), typeof(GatewayApiAuthOptions),
            ["Unsupported gateway authentication mode."]);
    }

}
