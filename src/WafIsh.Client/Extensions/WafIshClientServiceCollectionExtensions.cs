using WafIsh.GatewayRegistry;
using GatewayGrpc = WafIsh.GatewayRegistry.GatewayRegistry;
using WafIsh.Models;
using WafIsh.Services;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WafIsh.Extensions;

/// <summary>
/// WafIsh client service registration extensions.
/// </summary>
public static class WafIshClientServiceCollectionExtensions
{
    /// <summary>
    /// Register this service as a YARP client with auto-registration to the gateway.
    /// Auto-registers route on startup, auto-unregisters on shutdown.
    /// The only required config is <c>GatewayUrl</c> (code or appsettings.json).
    /// </summary>
    /// <param name="services">IServiceCollection</param>
    /// <param name="configureOptions">Optional override for registration options.</param>
    /// <example>
    /// <code>
    /// // Minimal - only config file needed:
    /// builder.Services.AddWafIshClient();
    /// // { "Gateway:Registration": { "GatewayUrl": "http://192.168.1.100:5000" } }
    ///
    /// // With code override:
    /// builder.Services.AddWafIshClient(o => {
    ///     o.GatewayUrl = "http://gateway:5000";
    ///     o.MatchPath = "/api/users/{**catch-all}";
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddWafIshClient(
        this IServiceCollection services,
        Action<GatewayRegistrationOptions>? configureOptions = null)
    {
        ConfigureClientInfrastructure(services, configureOptions);
        services.AddSingleton<GatewayAutoRegistrationClient>();
        services.AddHostedService<GatewayRegistrationHostedService>();
        return services;
    }

    /// <summary>
    /// Register client services without the hosted service (used by gateway to support upstream registration).
    /// </summary>
    public static IServiceCollection AddWafIshClientInternal(
        this IServiceCollection services)
    {
        ConfigureClientInfrastructure(services, null);
        services.AddSingleton<GatewayAutoRegistrationClient>();
        return services;
    }

    private static void ConfigureClientInfrastructure(
        IServiceCollection services,
        Action<GatewayRegistrationOptions>? configureOptions)
    {
        services.AddHttpClient();
        ConfigureRegistrationOptions(services, configureOptions);
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GatewayRegistrationOptions>>().Value;
            var gatewayUrl = options.GrpcUrl ?? options.GatewayUrl ?? "http://localhost:5000";

            // gRPC over HTTP requires HTTP/2, but .NET 9 Kestrel does not support
            // HTTP/1.1 + HTTP/2 on the same cleartext port. Use a dedicated HTTP/2 port.
            // Priority: options.GrpcPort > Gateway:Grpc:Port config > port+1 fallback.
            // See UseYarpKestrelAutoConfig / KestrelExtensions.TryParseAndConfigure.
            if (string.IsNullOrWhiteSpace(options.GrpcUrl) && Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gwUri) &&
                gwUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                var grpcPort = options.GrpcPort ?? (gwUri.Port + 1);
                gatewayUrl = $"{gwUri.Scheme}://{gwUri.Host}:{grpcPort}{gwUri.PathAndQuery}";
            }

            // Use platform certificate validation for every environment.
            var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };

            return GrpcChannel.ForAddress(gatewayUrl, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
        });
        services.AddSingleton(sp =>
        {
            var channel = sp.GetRequiredService<GrpcChannel>();
            return new GatewayGrpc.GatewayRegistryClient(channel);
        });
        services.AddSingleton<KestrelAutoConfigService>();
    }

    internal static void ConfigureRegistrationOptions(
        IServiceCollection services,
        Action<GatewayRegistrationOptions>? configureOptions)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
        services.AddOptions<GatewayRegistrationOptions>()
            .Configure<IConfiguration>((o, c) =>
                c.GetSection(GatewayRegistrationOptions.SectionName).Bind(o));

        if (configureOptions != null)
            services.Configure(configureOptions);

        services.AddSingleton<IValidateOptions<GatewayRegistrationOptions>, SkipValidation>();
    }

    /// <summary>Skips options validation (all config is optional).</summary>
    internal sealed class SkipValidation : IValidateOptions<GatewayRegistrationOptions>
    {
        public ValidateOptionsResult Validate(string? name, GatewayRegistrationOptions options)
            => !string.IsNullOrWhiteSpace(options.InstanceId) && options.UseGrpcRegistration != true
                ? ValidateOptionsResult.Fail("InstanceId requires gRPC registration.")
                : ValidateOptionsResult.Success;
    }
}
