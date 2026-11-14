using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SampleGateway;
using Wafish.AppHost;
using Wafish.Dashboard.Infrastructure.Deployment;

internal static class SampleDeploymentChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Sample deployment: topology identifiers isolate state, credentials, volumes, and restart locks", TopologyAsync);
        await run("Sample deployment: Sonar readiness accepts only UP and bounds malformed or unavailable replies", SonarAsync);
        await run("Sample deployment: operator links follow bound HTTPS endpoints and custom UI prefixes", OperatorAsync);
    }
    private static Task TopologyAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wafish-topology-check-" + Guid.NewGuid().ToString("N"));
        IConfiguration Configuration(string id, bool legacy = false) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TopologyId"] = id, ["GatewayDataDirectory"] = directory, ["UseLegacyTopologyState"] = legacy.ToString() }).Build();
        try
        {
            var first = TopologyState.Read(Configuration("one"), directory);
            var second = TopologyState.Read(Configuration("two"), directory);
            Check(first.DataDirectory != second.DataDirectory && first.Parameter("control-plane-password") != second.Parameter("control-plane-password")
                && first.Volume("sonarqube-data") != second.Volume("sonarqube-data"), "Topology state identifiers collided.");
            using (first.Acquire())
            using (second.Acquire())
            {
                File.WriteAllText(Path.Combine(first.DataDirectory, "persisted"), "one");
                File.WriteAllText(Path.Combine(second.DataDirectory, "persisted"), "two");
                try { using var conflict = first.Acquire(); throw new Exception("Concurrent topology acquired the same data directory."); }
                catch (InvalidOperationException) { }
            }
            using (first.Acquire()) Check(File.ReadAllText(Path.Combine(first.DataDirectory, "persisted")) == "one", "Restart lost its state.");
            Check(File.ReadAllText(Path.Combine(second.DataDirectory, "persisted")) == "two", "Restart changed another topology.");
            var legacy = TopologyState.Read(Configuration("default", true), directory);
            Check(legacy.DataDirectory == directory && legacy.Parameter("control-plane-password") == "control-plane-password"
                && legacy.Volume("sonarqube-data") == "wafish-sonarqube-data", "Legacy compatibility renamed existing state.");
            foreach (var id in new[] { "", "../escape", "Upper", "a/b", new string('a', 41), "1invalid" })
            {
                try { TopologyState.Read(Configuration(id), directory); throw new Exception("Invalid topology identifier accepted."); }
                catch (ArgumentException) { }
            }
            try { TopologyState.Read(Configuration("other", true), directory); throw new Exception("Legacy state was shared under another identifier."); }
            catch (ArgumentException) { }
            Check(TopologyState.Read(new ConfigurationBuilder().Build(), directory).DataDirectory == Path.Combine(directory, ".data", "default"), "Default topology path changed unexpectedly.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
    private static async Task SonarAsync()
    {
        foreach (var body in new[] { "{\"status\":\"UP\"}", "{\"status\":\"STARTING\"}", "{\"status\":\"DOWN\"}", "{\"status\":\"DB_MIGRATION_NEEDED\"}", "{}", "[]", "{\"status\":3}", "{", "null" })
        {
            using var handler = new StatusHandler(body);
            var check = new SonarQubeHealthCheck(new Factory(handler), _ => Task.FromResult<string?>("http://localhost"));
            var result = await check.CheckHealthAsync(new());
            Check((result.Status == HealthStatus.Healthy) == (body == "{\"status\":\"UP\"}"), "Sonar readiness accepted a non-UP response.");
        }
        foreach (var mode in new[] { "status", "unavailable", "timeout" })
        {
            using var handler = new StatusHandler("{}", mode);
            var check = new SonarQubeHealthCheck(new Factory(handler), _ => Task.FromResult<string?>("http://localhost"));
            Check((await check.CheckHealthAsync(new()).WaitAsync(TimeSpan.FromSeconds(5))).Status == HealthStatus.Unhealthy, "Sonar failure was reported ready.");
        }
        using var valid = new StatusHandler("{\"status\":\"UP\"}");
        Check((await new SonarQubeHealthCheck(new Factory(valid), _ => Task.FromResult<string?>(null)).CheckHealthAsync(new())).Status == HealthStatus.Unhealthy,
            "Missing Sonar endpoint was reported ready.");
    }
    private static Task OperatorAsync()
    {
        EndpointRoleMap[] roles = [new() { EndpointName = "Gateway", Port = 8443, Role = "Dashboard" }, new() { EndpointName = "Grpc", Port = 8444, Role = "All" }];
        Check(OperatorUrl.Resolve(["http://127.0.0.1:8444", "https://127.0.0.1:8443"], roles, "/nested/ops/") == "https://127.0.0.1:8443/nested/ops/", "Operator link ignored transport, role, or prefix.");
        Check(OperatorUrl.Resolve(["http://0.0.0.0:8080"], [], "console") == "http://localhost:8080/console/", "Wildcard binding was not converted into a browser authority.");
        Check(OperatorUrl.Resolve(["https://[::]:8443"], roles, "console") == "https://localhost:8443/console/", "IPv6 wildcard link was invalid.");
        Check(OperatorUrl.Resolve(["invalid", "ftp://localhost:8443", "http://localhost:8444"], roles, "console") is null, "Non-browser endpoint produced an operator link.");
        Check(OperatorUrl.Resolve(["http://localhost:9999"], [new() { EndpointName = "Gateway", Port = 0, Role = "All" }], "console") == "http://localhost:9999/console/", "Ephemeral binding used its configured zero port.");
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { Timeout = TimeSpan.FromMilliseconds(50) }; }
    private sealed class StatusHandler(string body, string mode = "") : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.AbsolutePath == "/api/system/status", "Sonar health requested the wrong route.");
            if (mode == "unavailable") throw new HttpRequestException("Synthetic connection failure.");
            if (mode == "timeout") await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(mode == "status" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
