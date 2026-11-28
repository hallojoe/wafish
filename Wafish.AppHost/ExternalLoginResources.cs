using Microsoft.Extensions.Configuration;

namespace Wafish.AppHost;

internal static class ExternalLoginResources
{
    internal static void AddExternalLogin(this IDistributedApplicationBuilder builder, TopologyState topology)
    {
        var gatewayPort = builder.Configuration.GetValue("ExternalLogin:GatewayPort", 7443);
        var identityPort = builder.Configuration.GetValue("ExternalLogin:IdentityPort", 7444);
        var gateway = builder.AddProject<Projects.SampleExternalLogin>("visitor-gateway", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint(port: gatewayPort, isProxied: false);
        var origin = $"https://localhost:{gatewayPort}"; // Browser callback; never a container-relative endpoint.
        var identity = $"https://localhost:{identityPort}";
        var secret = builder.AddParameter(topology.Parameter("visitor-client-secret"), new GenerateParameterDefault { MinLength = 40 }, secret: true, persist: true);
        var writerPassword = builder.AddParameter(topology.Parameter("visitor-writer-password"), new GenerateParameterDefault { MinLength = 24 }, secret: true, persist: true);
        var readerPassword = builder.AddParameter(topology.Parameter("visitor-reader-password"), new GenerateParameterDefault { MinLength = 24 }, secret: true, persist: true);
        // Aspire 13.6 exposes certificate wiring through this preview API.
#pragma warning disable ASPIRECERTIFICATES001
        var identityServer = builder.AddKeycloak("visitor-identity", adminPassword: builder.AddParameter(topology.Parameter("visitor-admin-password"), new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true))
            .WithImageTag("26.6.4")
            .WithRealmImport(Path.Combine(builder.AppHostDirectory, "ExternalLoginRealm"))
            .WithHttpsEndpoint(port: identityPort, targetPort: 8443, isProxied: false)
            .WithHttpsCertificateConfiguration(context =>
            {
                context.EnvironmentVariables["KC_HTTPS_CERTIFICATE_FILE"] = context.CertificatePath;
                context.EnvironmentVariables["KC_HTTPS_CERTIFICATE_KEY_FILE"] = context.KeyPath;
                return Task.CompletedTask;
            })
            .WithEnvironment("KC_HOSTNAME", identity)
            .WithEnvironment("WAFISH_GATEWAY_ORIGIN", origin)
            .WithEnvironment("WAFISH_CLIENT_SECRET", secret)
            .WithEnvironment("WAFISH_WRITER_PASSWORD", writerPassword)
            .WithEnvironment("WAFISH_READER_PASSWORD", readerPassword);
#pragma warning restore ASPIRECERTIFICATES001
        var sessions = builder.AddRedis("visitor-sessions", password: builder.AddParameter(topology.Parameter("visitor-redis-password"), new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true));
        var site = builder.AddProject<Projects.SampleExternalLogin>("visitor-static", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint().WithEnvironment("Sample__Role", "static");
        var api = builder.AddProject<Projects.SampleExternalLogin>("visitor-support", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint().WithEnvironment("Sample__Role", "api")
            .WithEnvironment("Sample__Authority", identity + "/realms/wafish")
            .WaitFor(identityServer);
        var gateways = new[] { gateway };
        if (builder.Configuration.GetValue<bool>("EnableSecondVisitorGateway"))
            gateways = [gateway, builder.AddProject<Projects.SampleExternalLogin>("visitor-gateway-secondary", options => options.ExcludeLaunchProfile = true)
                .WithHttpsEndpoint(port: builder.Configuration.GetValue("ExternalLogin:ReplicaPort", 7445), isProxied: false)];
        foreach (var instance in gateways)
        instance.WithEnvironment("Sample__Role", "gateway")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("Sample__StaticOrigin", site.GetEndpoint("https"))
            .WithEnvironment("Sample__ApiOrigin", api.GetEndpoint("https"))
            .WithEnvironment("Gateway__Storage__Sqlite__ConnectionString", $"Data Source={Path.Combine(topology.DataDirectory, "visitor-" + Guid.NewGuid().ToString("N") + ".db")}")
            .WithEnvironment("Gateway__ExternalLogin__RedisConnectionReference", "Secrets:VisitorRedis")
            .WithEnvironment("Secrets__VisitorRedis", sessions.Resource.ConnectionStringExpression)
            .WithEnvironment("Gateway__ExternalLogin__Namespace", "visitor-" + topology.Id)
            .WithEnvironment("Gateway__ExternalLogin__KeyRingPath", Path.Combine(topology.DataDirectory, "visitor-keys"))
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Id", "website")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Origin", origin)
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__Id", "keycloak")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__DisplayName", "Local Keycloak")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__Authority", identity + "/realms/wafish")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__ClientId", "wafish")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__ClientSecretReference", "Secrets:VisitorClient")
            .WithEnvironment("Secrets__VisitorClient", secret)
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__Scopes__0", "openid")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__Scopes__1", "profile")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__Providers__0__Scopes__2", "support:write")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__ApiTargets__0__Id", "support")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__ApiTargets__0__Audience", "support-api")
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__ApiTargets__0__Origins__0", api.GetEndpoint("https"))
            .WithEnvironment("Gateway__ExternalLogin__Sites__0__ApiTargets__0__RequiredScopes__0", "support:write")
            .WithHttpHealthCheck("/ready", endpointName: "https")
            .WithUrlForEndpoint("https", url => { url.Url = "/"; url.DisplayText = instance.Resource.Name == "visitor-gateway" ? "Visitor login sample" : "Visitor replica"; })
            .WaitFor(identityServer).WaitFor(sessions).WaitFor(site).WaitFor(api);
    }
}
