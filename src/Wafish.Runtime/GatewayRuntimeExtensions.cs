using Wafish.Plugin.ExternalLogin;
using System.Collections.Concurrent;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.Dashboard.Infrastructure.Performance;
using Wafish.Infrastructure.Performance;
using Wafish.Plugins.CircuitBreaker.Services;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Dashboard.Modules.GatewayConfig.Services;
using Wafish.Plugin.Cache;
using Wafish.Plugin.Redirect;
using Wafish.Plugin.ClaimsTransform;
using Wafish.Plugin.RouteAuthorization;
using Wafish.Plugin.Aggregation;
using Wafish.Runtime.InternalExecution;
using Wafish.Plugin.CircuitBreaker;
using Wafish.Plugin.Compression;
using Wafish.Plugin.Metrics;
using Wafish.Plugin.ProxyLog;
using Wafish.Plugin.ProxyLog.Services;
using Wafish.Plugin.RateLimit;
using Wafish.Plugin.RateLimit.Redis;
using Wafish.Plugin.Retry;
using Wafish.Plugin.ServiceDiscovery;
using Wafish.Plugin.Waf;
using Wafish.Models;
using Wafish.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Yarp.ReverseProxy.Transforms.Builder;
using Wafish.Infrastructure.State;
using Wafish.Infrastructure.Resilience;
using Wafish.Plugins;
using Wafish.Extensions;
using Wafish.Infrastructure.Middleware;
using Wafish.Dashboard.Infrastructure.Yarp;
using Microsoft.AspNetCore.Routing;
using Wafish.Dashboard.Infrastructure.Deployment;
using Microsoft.AspNetCore.ResponseCompression;

namespace Wafish.Runtime;

/// <summary>Registers the gateway runtime without administration controllers or Razor views.</summary>
public static class GatewayRuntimeExtensions
{
    public static IServiceCollection AddWafishRuntime(this IServiceCollection services, bool enableRegistration = false)
    {
        if (services.Any(d => d.ServiceType == typeof(RuntimeMarker)))
        {
            return services;
        }

        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
        services.AddSingleton<RuntimeMarker>();
        services.AddWafish(enableRegistration: enableRegistration);
        services.AddMemoryCache();
        services.AddOptions<ResponseCacheBudgetOptions>().BindConfiguration("Gateway:ResponseCache");
        services.AddSingleton<ResponseCacheStore>();
        services.TryAddSingleton<RecyclableMemoryStreamManager>();
        services.TryAddSingleton<LockFreeStatistics>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransformProvider, DownstreamCaptureTransformProvider>());
        services.AddOptions<GatewayMiddlewareOptions>().BindConfiguration("Gateway:Dashboard");
        services.AddOptions<DeploymentOptions>().BindConfiguration(DeploymentOptions.SectionName);
        services.TryAddSingleton<EndpointRoleResolver>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DeploymentOptions>>().Value;
            options.ResolvedEndpoints.Clear();
            return new EndpointRoleResolver(sp.GetRequiredService<IConfiguration>(), options);
        });
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["text/plain", "text/css", "text/javascript", "text/xml", "application/javascript",
                "application/json", "application/xml", "application/xml-dtd", "application/atom+xml", "application/octet-stream",
                "image/svg+xml", "font/woff", "font/woff2", "font/ttf", "application/wasm"];
        });
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Optimal);
        services.AddOptions<ProxyLogPluginOptions>().BindConfiguration("Gateway:ProxyLog");
        services.TryAddSingleton<IConfigChangeAuditLog, ConfigChangeAuditLog>();
        services.TryAddSingleton<ConfigChangeAuditLog>(sp => (ConfigChangeAuditLog)sp.GetRequiredService<IConfigChangeAuditLog>());
        services.TryAddSingleton<ICircuitStateStore, InMemoryCircuitStateStore>();
        services.TryAddSingleton<IDestinationMaintenanceStore, InMemoryDestinationMaintenanceStore>();
        services.TryAddSingleton<IDestinationCandidateCoordinator, Wafish.Dashboard.Infrastructure.Resilience.DestinationCandidateCoordinator>();
        services.TryAddSingleton<IRateLimiterStore, InMemoryRateLimiterStore>();
        services.AddHostedService(sp => sp.GetRequiredService<DynamicYarpConfigService>());
        services.TryAddSingleton<ProxyLogRuntimeSettings>();
        services.AddSingleton<LogSettingsService>();
        services.AddHostedService(sp => sp.GetRequiredService<LogSettingsService>());
        services.AddSingleton<IProxyLogStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ProxyLogPluginOptions>>().Value;
            var runtimeSettings = sp.GetRequiredService<ProxyLogRuntimeSettings>();
            return new ProxyLogStore(
                opts.LogBufferCapacity,
                persistenceEnabled: opts.LogPersistenceEnabled,
                runtimeSettings: runtimeSettings);
        });
        services.AddSingleton<ProxyLogStore>(sp => (ProxyLogStore)sp.GetRequiredService<IProxyLogStore>());
        services.AddSingleton<LogSanitizer>();
        services.AddSingleton<ILogSanitizer>(sp => sp.GetRequiredService<LogSanitizer>());

        // Log sampling + filtering (extracted from YarpRequestCaptureMiddleware)
        services.AddSingleton<ILogSampler, LogSampler>();
        services.AddSingleton<ILogFilter, LogFilter>();
        services.AddSingleton<IProxyLogCapture, ProxyLogCapture>();

        // SqliteProxyLogWriter: converts LogEntry → Entity and delegates to IProxyLogRepository
        services.AddSingleton<SqliteProxyLogWriter>();
        // AsyncLogPersistenceService: background service that reads from Channel and writes batches to SQLite
        services.AddSingleton<AsyncLogPersistenceService>();
        services.AddSingleton<IProxyLogPersistenceService>(sp => sp.GetRequiredService<AsyncLogPersistenceService>());
        services.AddSingleton<IPluginRuntimeResource>(sp => sp.GetRequiredService<AsyncLogPersistenceService>());

        services.AddSingleton<IGatewayPlugin, CircuitBreakerPlugin>();
        services.AddSingleton<IGatewayPlugin, RequestRetryPlugin>();
        services.AddOptions<ClusterRetryBudgetOptions>().BindConfiguration("Gateway:RetryBudget");
        services.TryAddSingleton<ClusterRetryBudget>();
        services.AddSingleton<IGatewayPlugin, RateLimitPlugin>();
        // Redis rate limits use a route connection override or Gateway:RateLimit:Redis configuration.
        services.AddSingleton<IGatewayPlugin, RedisRateLimitPlugin>();
        services.AddOptions<RedisRateLimitStoreOptions>().BindConfiguration("Gateway:RateLimit:Redis");
        services.AddSingleton<IDistributedRateLimitStore, RedisLuaRateLimitStore>();
        services.AddSingleton<IGatewayPlugin, WafPlugin>();
        services.AddSingleton<IRoutePluginCompiler, WafConfigurationCompiler>();
        services.AddSingleton<IGatewayPlugin, ProxyLogPlugin>();
        services.AddSingleton<IGatewayPlugin, ResponseCachePlugin>();
        services.AddSingleton<IGatewayPlugin, TrafficMetricsPlugin>();
        services.AddSingleton<IGatewayPlugin, ClusterMetricsPlugin>();
        services.AddSingleton<IGatewayPlugin, HttpServiceDiscoveryPlugin>();
        services.AddSingleton<IGatewayPlugin, CompressionPlugin>();
        services.AddAuthorization();
        services.AddRateLimiter(_ => { });
        services.AddRequestTimeouts();
        services.AddSingleton<InternalRouteExecutor>();
        services.AddSingleton<IInternalRouteExecutor>(sp => sp.GetRequiredService<InternalRouteExecutor>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<InternalRouteExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransformProvider, InternalForwarderTransformProvider>());
        services.TryAddSingleton<RoutePolicyPipelineSupport>();
        services.AddSingleton<IGatewayPlugin, AggregationPlugin>();
        services.AddSingleton<IRoutePluginCompiler, AggregationCompiler>();
        services.AddSingleton<AggregationAdmission>();
        services.AddSingleton<AggregationHandler>();
        services.AddSingleton<IGatewayPlugin, ExternalLoginPlugin>();
        services.AddSingleton<IRoutePluginCompiler, ExternalLoginCompiler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransformProvider, ExternalLoginTransformProvider>());
        services.AddSingleton<IGatewayPlugin, RouteAuthorizationPlugin>();
        services.AddSingleton<IRoutePluginCompiler, RouteAuthorizationCompiler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransformProvider, RouteAuthorizationTransformProvider>());
        services.AddSingleton<IGatewayPlugin, ClaimsTransformPlugin>();
        services.AddSingleton<IRoutePluginCompiler, ClaimsTransformCompiler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransformProvider, ClaimsTransformProvider>());
        services.AddSingleton<IGatewayPlugin, RedirectPlugin>();
        services.AddSingleton<IRoutePluginCompiler, RedirectConfigurationCompiler>();
        services.AddSingleton<PluginMetricStore>();
        services.AddSingleton<ExternalGatewayPluginHost>();
        services.AddSingleton<GatewayPluginManager>();
        services.AddSingleton<IGatewayPluginManager>(provider => provider.GetRequiredService<GatewayPluginManager>());
        services.AddSingleton<IPluginDashboardBuilder, PluginDashboardBuilder>();
        services.AddSingleton<PluginDashboardInitializer>();
        services.AddSingleton<IPluginRuntimeDomainManager, PluginRuntimeDomainManager>();
        services.AddSingleton<IConfigurationChangeSetRuntime, ConfigurationChangeSetRuntime>();
        services.AddHostedService<RegistrationLeaseReaper>();
        services.AddSingleton<IPluginBindingMutationService, PluginBindingMutationService>();
        services.AddSingleton(provider => (PluginRuntimeDomainManager)provider.GetRequiredService<IPluginRuntimeDomainManager>());
        services.AddSingleton<IPluginDrainDiagnostics>(provider => provider.GetRequiredService<PluginRuntimeDomainManager>());
        services.AddSingleton<IPluginActivationPreviewService>(provider => provider.GetRequiredService<PluginRuntimeDomainManager>());
        services.AddHostedService<PluginRuntimeDomainInitializer>();
        services.AddSingleton<IPluginActivationState>(provider => provider.GetRequiredService<GatewayPluginManager>());
        services.AddSingleton<GatewayPluginExecutionPlanProvider>();
        services.AddSingleton<IPluginManifestCatalog>(provider => provider.GetRequiredService<IGatewayPluginManager>());
        services.AddSingleton<IPluginConfigurationSchemaValidator, PluginConfigurationSchemaValidator>();
        services.AddSingleton<IPluginConfigurationMigrationService, PluginConfigurationMigrationService>();
        services.AddSingleton<IPluginConfigurationMigrator, CircuitBreakerConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, RetryConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, WafConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, RateLimitConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, ProxyLogConfigMigrator>();
        services.AddSingleton<CircuitBreakerWarmupService>();
        services.AddSingleton<IPluginRuntimeResource>(provider => provider.GetRequiredService<CircuitBreakerWarmupService>());
        services.AddSingleton<IPluginRuntimeResource, ServiceDiscoveryRefreshService>();
        services.AddHttpClient("service-discovery");
        services.AddSingleton<PluginResourceLifecycleCoordinator>();
        services.AddSingleton<IPluginResourceLifecycleCoordinator>(provider => provider.GetRequiredService<PluginResourceLifecycleCoordinator>());
        services.AddHostedService(provider => provider.GetRequiredService<PluginResourceLifecycleCoordinator>());
        services.AddSingleton<PluginResourceMonitor>();
        services.AddSingleton<IPluginResourceMonitor>(provider => provider.GetRequiredService<PluginResourceMonitor>());

        return services;
    }

    public static IApplicationBuilder UseWafishRuntime(this IApplicationBuilder app)
    {
        app.Use((context, next) => context.RequestServices.GetRequiredService<PluginRuntimeDomainManager>()
            .InvokeMiddlewareAsync(context, next));
        return app;
    }

    public static IEndpointConventionBuilder MapWafishProxy(this IEndpointRouteBuilder endpoints,
        Action<IReverseProxyApplicationBuilder>? configure = null, bool useBuiltInPipeline = true)
    {
        if (!useBuiltInPipeline)
        {
            endpoints.ServiceProvider.GetService<RoutePolicyPipelineSupport>()?.Disable();
            if (endpoints.ServiceProvider.GetService<IGatewaySnapshotPublisher>()?.Current.Routes.Any(r =>
                r.Metadata?.ContainsKey("wafish:request-aggregation") == true || r.Metadata?.ContainsKey("wafish:route-authorization") == true || r.Metadata?.ContainsKey("wafish:claims-transform") == true || r.Metadata?.ContainsKey("wafish:external-login") == true) == true)
                throw new InvalidOperationException("Active endpoint policies require the built-in proxy pipeline.");
        }
        var conventions = endpoints.MapReverseProxy(proxy =>
        {
            proxy.Use((context, next) => { InternalRouteExecutor.RestoreProxyFeature(context); return next(context); });
            proxy.UseMiddleware<Wafish.Dashboard.Infrastructure.Resilience.DestinationMaintenanceMiddleware>();
            if (useBuiltInPipeline)
            {
                proxy.UseMiddleware<BuiltinTransformMiddleware>();
                proxy.Use((context, next) => context.RequestServices.GetRequiredService<PluginRuntimeDomainManager>()
                    .InvokeProxyPipelineAsync(context, next));
            }
            configure?.Invoke(proxy);
        });
        if (useBuiltInPipeline)
        {
            conventions.Add(endpoint =>
            {
                ExternalLoginExtensions.ConfigureEndpoint(endpoint);
                var forward = endpoint.RequestDelegate!;
                endpoint.RequestDelegate = context => RouteAuthorizationMiddleware.InvokeAsync(context, authorized =>
                    ClaimsTransformProvider.InvokeAsync(authorized, inner => inner.RequestServices.GetRequiredService<PluginRuntimeDomainManager>()
                        .InvokeAggregationPipelineAsync(inner, aggregate => aggregate.RequestServices.GetRequiredService<PluginRuntimeDomainManager>()
                            .InvokeRedirectPipelineAsync(aggregate, forward))));
            });
        }
        return conventions;
    }

    private sealed class RuntimeMarker;
}
