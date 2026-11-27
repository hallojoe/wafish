using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Wafish.Models;
using Yarp.ReverseProxy.Configuration;
using Fixture = ConfigurationTransactionChecks.Fixture;

internal static class RegistrationMigrationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Registration migration: absent metadata and matching aliases preserve unrelated values", async () =>
        {
            await using var host = await Fixture.Create();
            var metadata = new Dictionary<string, string>
            {
                ["aneiang.registration.service"] = "service", ["wafish.registration.service"] = "service",
                ["aneiang.custom"] = "retained"
            };
            Applied(await host.Config.ImportBatchAsync([Cluster() with { Metadata = metadata }], [Route()], "grpc"));
            await using var restart = await Fixture.Create(host.Directory);
            var cluster = restart.Config.GetClusters().Single();
            Check(cluster.Metadata!["aneiang.custom"] == "retained" && cluster.Metadata["wafish.registration.service"] == "service",
                "Migration changed unrelated metadata or matching aliases.");
            Check(!cluster.Metadata.ContainsKey("aneiang.registration.service"), "Matching alias was not removed.");
            Check(cluster.Destinations!["d1"].Metadata is null && restart.Config.GetRoutes().Single().Metadata is null,
                "Migration invented metadata.");
        });
        await run("Registration migration: old ownership permits new ports and preserves peers", async () =>
        {
            await using var host = await Fixture.Create();
            Applied(await host.Config.RegisterInstanceAsync("service", "one", Cluster(), [Route()]));
            Applied(await host.Config.RegisterInstanceAsync("service", "two", Cluster(), [Route()]));
            var uid = host.Config.GetDynamicConfig()!.Routes.Single().RouteUid;
            await LegacyAsync(host);
            await using var restart = await Fixture.Create(host.Directory);
            Applied(await restart.Config.RegisterInstanceAsync("service", "one", Cluster("http://localhost:18618/"), [Route()]));
            var destinations = restart.Config.GetClusters().Single().Destinations!;
            Check(destinations.Count == 2 && destinations["one:d1"].Address == "http://localhost:18618/" &&
                destinations["two:d1"].Address == "http://localhost:28527/", "Port replacement lost a peer.");
            Check(restart.Config.GetDynamicConfig()!.Routes.Single().RouteUid == uid, "Migration changed route identity.");
            Check(!JsonSerializer.Serialize(await restart.Store.LoadAsync()).Contains("aneiang.registration."), "Legacy keys remain stored.");
            await using var again = await Fixture.Create(host.Directory);
            Check(again.Config.IsInstanceRegistered("service", "one"), "Repeated startup lost ownership.");
            Applied(await again.Config.UnregisterInstanceAsync("service", "one"));
            Check(again.Config.IsInstanceRegistered("service", "two"), "Removal lost a peer.");
        });
        await run("Registration migration: lease credentials and drain state survive the rename", async () =>
        {
            await using var host = await Fixture.Create();
            var token = new string('A', 64);
            Applied((await host.Config.RegisterLeasedInstanceAsync("service", "one", Cluster(), [Route()], token, 3600)).Outcome);
            Applied((await host.Config.RenewInstanceLeaseAsync("service", "one", token, drain: true)).Outcome);
            await LegacyAsync(host);
            await using var restart = await Fixture.Create(host.Directory);
            Check(restart.Config.IsLeasedInstance("service", "one"), "Migration removed lease protection.");
            Check(!(await restart.Config.RegisterInstanceAsync("service", "one", Cluster(), [Route()])).Success, "Legacy lease was bypassed.");
            Check(!(await restart.Config.RenewInstanceLeaseAsync("service", "one", new string('B', 64))).Outcome.Success, "Wrong lease accepted.");
            var renewed = await restart.Config.RenewInstanceLeaseAsync("service", "one", token);
            Applied(renewed.Outcome);
            Check(renewed.Lease!.Draining, "Migration cleared drain state.");
        });
        await run("Registration migration: conflicting ownership fails without changing storage", async () =>
        {
            await using var host = await Fixture.Create();
            Applied(await host.Config.RegisterInstanceAsync("service", "one", Cluster(), [Route()]));
            await LegacyAsync(host);
            await host.Sql("UPDATE yarp_clusters SET config_json = json_set(config_json, '$.Metadata.\"wafish.registration.service\"', 'other');");
            var before = JsonSerializer.Serialize(await host.Store.LoadAsync());
            await using var restart = await Fixture.Create(host.Directory, start: false);
            try
            {
                await ((IHostedService)restart.Config).StartAsync(default);
                throw new Exception("Conflicting ownership accepted.");
            }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Conflicting legacy")) { }
            Check(before == JsonSerializer.Serialize(await host.Store.LoadAsync()), "Failed migration changed storage.");
            Check(!restart.Publisher.Current.Clusters.Any(), "Failed migration published configuration.");
        });
        await run("Registration migration: failed persistence leaves legacy storage intact", async () =>
        {
            await using var host = await Fixture.Create();
            Applied(await host.Config.RegisterInstanceAsync("service", "one", Cluster(), [Route()]));
            await LegacyAsync(host);
            var before = JsonSerializer.Serialize(await host.Store.LoadAsync());
            await host.Sql("CREATE TRIGGER reject_migration BEFORE UPDATE ON configuration_version BEGIN SELECT RAISE(ABORT, 'injected migration failure'); END;");
            await using var restart = await Fixture.Create(host.Directory, start: false);
            try
            {
                await ((IHostedService)restart.Config).StartAsync(default);
                throw new Exception("Failed migration accepted.");
            }
            catch (ConfigurationUpdateException) { }
            Check(before == JsonSerializer.Serialize(await host.Store.LoadAsync()), "Failed write changed storage.");
            Check(!restart.Publisher.Current.Clusters.Any(), "Failed write published configuration.");
        });
    }

    // Reproduce the metadata written before the Aneiang-to-Wafish rename.
    private static Task LegacyAsync(Fixture host) => host.Sql("""
        UPDATE yarp_clusters SET config_json = replace(config_json, 'wafish.registration.', 'aneiang.registration.');
        UPDATE yarp_routes SET config_json = replace(config_json, 'wafish.registration.', 'aneiang.registration.'),
            metadata = replace(metadata, 'wafish.registration.', 'aneiang.registration.');
        UPDATE yarp_destinations SET metadata = replace(metadata, 'wafish.registration.', 'aneiang.registration.');
        """);

    private static ClusterConfig Cluster(string address = "http://localhost:28527/") => new()
    {
        ClusterId = "shared", LoadBalancingPolicy = "RoundRobin",
        Destinations = new Dictionary<string, DestinationConfig> { ["d1"] = new() { Address = address } }
    };
    private static RouteConfig Route() => new() { RouteId = "service", ClusterId = "shared", Match = new() { Path = "/api/{**rest}" } };
    private static void Applied(RouteOperationResult result) => Check(result.Success, result.Message);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
