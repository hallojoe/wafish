using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace Wafish.Extensions;

/// <summary>
/// Kestrel configuration extensions support HTTP/1.1 and HTTP/2 for gRPC.
/// </summary>
public static class KestrelExtensions
{
    /// <summary>
    /// Configure Kestrel to listen on 0.0.0.0 for remote access.
    /// Detect and override these configuration sources:
    /// 1. "Urls" setting
    /// 2. "Kestrel:EndPoints" section
    /// 3. ASPNETCORE_URLS environment variable
    /// </summary>
    /// <param name="builder">WebApplicationBuilder</param>
    /// <param name="forceAnyIP">Force listening on 0.0.0.0, even when the configuration specifies localhost.</param>
    /// <returns>WebApplicationBuilder</returns>
    /// <example>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.UseYarpKestrelAutoConfig();
    /// </code>
    /// </example>
    public static WebApplicationBuilder UseYarpKestrelAutoConfig(
        this WebApplicationBuilder builder,
        bool forceAnyIP = true)
    {
        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            ConfigureFromAllSources(context.Configuration, options, forceAnyIP);
        });

        return builder;
    }


    /// <summary>
    /// Configure Kestrel to listen on 0.0.0.0 for remote access.
    /// Detect and override these configuration sources:
    /// 1. "Urls" setting
    /// 2. "Kestrel:EndPoints" section
    /// 3. ASPNETCORE_URLS environment variable
    /// </summary>
    /// <param name="builder">WebApplicationBuilder</param>
    /// <param name="forceAnyIP">Force listening on 0.0.0.0, even when the configuration specifies localhost.</param>
    /// <returns>WebApplicationBuilder</returns>
    /// <example>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.UseYarpKestrelAutoConfig();
    /// </code>
    /// </example>
    public static IWebHostBuilder UseYarpKestrelAutoConfig(
        this IWebHostBuilder builder,
        bool forceAnyIP = true)
    {
        builder.ConfigureKestrel((context, options) =>
        {
            ConfigureFromAllSources(context.Configuration, options, forceAnyIP);
        });

        return builder;
    }

    /// <summary>
    /// Config key for explicit gRPC port override on the gateway.
    /// When not set, defaults to <c>mainPort + 1</c> in HTTP mode.
    /// </summary>
    private const string GrpcPortConfigKey = "Gateway:Grpc:Port";

    /// <summary>
    /// Read all configuration sources and configure Kestrel endpoints.
    /// </summary>
    private static void ConfigureFromAllSources(
        IConfiguration configuration,
        KestrelServerOptions options,
        bool forceAnyIP)
    {
        var configured = false;

        // 1. Read the "Kestrel:EndPoints" section first (highest priority).
        configured |= ConfigureFromKestrelSection(configuration, options, forceAnyIP);

        // 2. Read the "Urls" setting.
        if (!configured)
        {
            configured |= ConfigureFromUrls(configuration, options, forceAnyIP);
        }

        // 3. Read the ASPNETCORE_URLS environment variable.
        if (!configured)
        {
            configured |= ConfigureFromEnvironment(options, forceAnyIP);
        }

        // 4. Use the default port if no configuration is available.
        //    Port 5000: HTTP/1.1 (Dashboard, YARP) + Port 5001: HTTP/2 (gRPC)
        if (!configured)
        {
            options.ListenAnyIP(5000, o => o.Protocols = HttpProtocols.Http1);
            options.ListenAnyIP(5001, o => o.Protocols = HttpProtocols.Http2);
        }
    }

    /// <summary>
    /// Resolves the gRPC port from config (<c>Gateway:Grpc:Port</c>) or falls back to <c>mainPort + 1</c>.
    /// </summary>
    private static int ResolveGrpcPort(IConfiguration? configuration, int mainPort)
    {
        if (configuration != null)
        {
            var configuredPort = configuration.GetValue<int?>(GrpcPortConfigKey);
            if (configuredPort.HasValue)
            {
                return configuredPort.Value;
            }
        }
        return mainPort + 1;
    }

    /// <summary>
    /// Read and configure the "Kestrel:EndPoints" section.
    /// Kestrel automatically binds endpoints when Kestrel:Endpoints is present.
    /// Return true without adding ListenAnyIP again. Duplicate bindings cause address conflicts.
    /// </summary>
    private static bool ConfigureFromKestrelSection(
        IConfiguration configuration,
        KestrelServerOptions options,
        bool forceAnyIP)
    {
        // Check both "EndPoints" (legacy) and "Endpoints" (standard) — .NET config is case-insensitive
        var kestrelSection = configuration.GetSection("Kestrel:EndPoints");
        if (!kestrelSection.Exists())
        {
            kestrelSection = configuration.GetSection("Kestrel:Endpoints");
        }
        if (!kestrelSection.Exists())
        {
            return false;
        }

        // Kestrel natively reads Kestrel:Endpoints from IConfiguration and binds the endpoints.
        // We must NOT call options.ListenAnyIP() here — that would create duplicate bindings.
        // Just return true to signal "configured" so that ConfigureFromAllSources doesn't
        // fall through to Urls/ASPNETCORE_URLS/default ports.

        // However, Kestrel's default protocol for a config-declared cleartext endpoint is
        // HTTP/1.1 only, and config-bound endpoints do not pick up ConfigureEndpointDefaults
        // in practice — gRPC calls fail with HTTP_1_1_REQUIRED. To serve gRPC in this mode,
        // declare a dedicated cleartext endpoint with "Protocols": "Http2", e.g.:
        //   "Kestrel": { "Endpoints": {
        //     "Grpc": { "Url": "http://0.0.0.0:5201", "Protocols": "Http2" } } }
        // Clients then reach it via the default GatewayUrl.Port + 1 offset (or GrpcPort).
        // The endpoint-defaults upgrade below is kept as a best-effort fallback for hosts
        // where it does apply; HTTPS endpoints negotiate HTTP/2 via ALPN regardless.
        options.ConfigureEndpointDefaults(o =>
        {
            if (o.Protocols == HttpProtocols.Http1)
            {
                o.Protocols = HttpProtocols.Http1AndHttp2;
            }
        });

        return true;
    }

    /// <summary>
    /// Read and apply the "Urls" setting.
    /// </summary>
    private static bool ConfigureFromUrls(
        IConfiguration configuration,
        KestrelServerOptions options,
        bool forceAnyIP)
    {
        var urls = configuration["Urls"];
        if (string.IsNullOrEmpty(urls))
        {
            return false;
        }

        var configured = false;
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseAndConfigure(url.Trim(), options, forceAnyIP, configuration))
            {
                configured = true;
            }
        }

        return configured;
    }

    /// <summary>
    /// Read and apply the ASPNETCORE_URLS environment variable.
    /// </summary>
    private static bool ConfigureFromEnvironment(
        KestrelServerOptions options,
        bool forceAnyIP)
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (string.IsNullOrEmpty(urls))
        {
            return false;
        }

        var configured = false;
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseAndConfigure(url.Trim(), options, forceAnyIP, configuration: null))
            {
                configured = true;
            }
        }

        return configured;
    }

    /// <summary>
    /// Parse the URL and configure the Kestrel listener.
    /// For HTTP endpoints without TLS, add an HTTP/2-only port for gRPC.
    /// Kestrel in .NET 9 cannot negotiate HTTP/1.1 and HTTP/2 together without TLS.
    /// Set the gRPC port with <c>Gateway:Grpc:Port</c>. The default is the main port plus one.
    /// </summary>
    private static bool TryParseAndConfigure(
        string url,
        KestrelServerOptions options,
        bool forceAnyIP,
        IConfiguration? configuration)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var port = uri.Port;
        var isHttps = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        var host = uri.Host;

        // When the user has explicitly declared 2+ Kestrel:Endpoints (multi-port / split mode),
        // we skip the auto-injected gRPC port. Each user-declared port should be honored
        // exactly as configured, with no implicit +1 offset.
        var hasExplicitMultiEndpoints = configuration != null
            && configuration.GetSection("Kestrel:Endpoints").GetChildren().Count() >= 2;
        var skipAutoGrpc = hasExplicitMultiEndpoints;

        // Split / multi-endpoint mode must honor the configured bind address.
        // Only legacy single-URL mode keeps the old forceAnyIP behavior for backward compatibility.
        var shouldListenAnyIP = IsAnyAddressHost(host) || (!hasExplicitMultiEndpoints && forceAnyIP);

        if (shouldListenAnyIP)
        {
            if (isHttps)
            {
                options.ListenAnyIP(port, listenOptions => listenOptions.UseHttps());
            }
            else
            {
                if (skipAutoGrpc)
                {
                    // Multi-endpoint mode: bind only the declared port, no auto gRPC.
                    options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http1AndHttp2);
                }
                else
                {
                    var grpcPort = ResolveGrpcPort(configuration, port);
                    // Main port: HTTP/1.1 only (Dashboard, YARP proxy, REST API)
                    options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http1);
                    // gRPC port: HTTP/2 only (h2c) — .NET 9 requires separate port for cleartext HTTP/2
                    options.ListenAnyIP(grpcPort, o => o.Protocols = HttpProtocols.Http2);
                }
            }
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = IPAddress.Loopback.ToString();
        }

        // Listen on the specified IP address, if supplied.
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            if (isHttps)
            {
                options.Listen(ipAddress, port, listenOptions => listenOptions.UseHttps());
            }
            else
            {
                if (skipAutoGrpc)
                {
                    // Multi-endpoint mode: bind only the declared port, no auto gRPC.
                    options.Listen(ipAddress, port, o => o.Protocols = HttpProtocols.Http1AndHttp2);
                }
                else
                {
                    var grpcPort = ResolveGrpcPort(configuration, port);
                    options.Listen(ipAddress, port, o => o.Protocols = HttpProtocols.Http1);
                    options.Listen(ipAddress, grpcPort, o => o.Protocols = HttpProtocols.Http2);
                }
            }
            return true;
        }

        return false;
    }

    private static bool IsAnyAddressHost(string host) =>
        string.IsNullOrWhiteSpace(host) || host is "0.0.0.0" or "*" or "+" or "::" or "[::]";
}
