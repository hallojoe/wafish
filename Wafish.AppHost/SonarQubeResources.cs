using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Wafish.AppHost;

internal static class SonarQubeResources
{
    public static IResourceBuilder<ContainerResource> AddSonarQube(this IDistributedApplicationBuilder builder, TopologyState topology)
    {
        var password = builder.AddParameter(topology.Parameter("sonar-db-password"),
            new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true);
        var postgres = builder.AddPostgres("sonar-postgres", password: password)
            .WithImageTag("18.1")
            .WithDataVolume(topology.Volume("sonar-postgres-data"));
        var database = postgres.AddDatabase("sonar-db", "sonar");
        var endpoint = postgres.GetEndpoint("tcp");

        var sonar = builder.AddContainer("sonarqube", "docker.io/library/sonarqube", "26.10.0.133078-community")
            .WithHttpEndpoint(targetPort: 9000, name: "http")
            .WithEnvironment("SONAR_JDBC_URL", ReferenceExpression.Create(
                $"jdbc:postgresql://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}/{database.Resource.DatabaseName}"))
            .WithEnvironment("SONAR_JDBC_USERNAME", postgres.Resource.UserNameReference)
            .WithEnvironment("SONAR_JDBC_PASSWORD", password)
            .WithVolume(topology.Volume("sonarqube-data"), "/opt/sonarqube/data")
            .WithVolume(topology.Volume("sonarqube-extensions"), "/opt/sonarqube/extensions")
            .WithVolume(topology.Volume("sonarqube-logs"), "/opt/sonarqube/logs")
            .WaitFor(database)
            .WithHealthCheck("sonarqube-ready");

        builder.Services.AddHttpClient("sonarqube-health", client => client.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration("sonarqube-ready",
            services => new SonarQubeHealthCheck(
                services.GetRequiredService<IHttpClientFactory>(),
                async cancellationToken => await sonar.GetEndpoint("http").GetValueAsync(cancellationToken)),
            failureStatus: HealthStatus.Unhealthy, tags: null, timeout: TimeSpan.FromSeconds(5)));
        return sonar;
    }
}
