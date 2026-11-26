using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Dashboard.Infrastructure.State;
using Wafish.Infrastructure.Middleware;
using Wafish.Plugin.RateLimit;
using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

internal static class QuotaGroupChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Local quotas: named groups share permits while routes, groups, and client partitions remain isolated", SharedAsync);
        await run("Local quotas: incompatible named policies fail before binding persistence and activation", ValidationAsync);
    }
    private static RouteConfig Route(string id) => new() { RouteId = id, ClusterId = "backend", Match = new() { Path = "/" + id } };
    private static PluginBindingEntity Binding(string route, string group, int permits = 2, string partition = "Global") => new()
    {
        Id = "quota-" + route, PluginId = "rate-limit", PluginVersion = "1.0", Enabled = true, Scope = PluginBindingScope.Route,
        ScopeId = route, ConfigJson = JsonSerializer.Serialize(new { enabled = true, quotaGroup = group, permitLimit = permits, window = "1h", partitionKey = partition })
    };
    private static async Task SharedAsync()
    {
        foreach (var partition in new[] { "Global", "Route", "IpAddress" })
        {
            var routes = new[] { Route("a"), Route("b"), Route("c"), Route("d"), Route("e") };
            var bindings = new[] { Binding("a", "inventory", partition: partition), Binding("b", "inventory", partition: partition),
                Binding("c", "orders", partition: partition), Binding("d", "", partition: partition), Binding("e", "", partition: partition) };
            var compiler = new GatewaySnapshotCompiler(new InMemoryPluginRepository(bindings));
            var publisher = new GatewaySnapshotPublisher(); publisher.Publish(await compiler.CompileAsync(routes, [], 1));
            using var store = new InMemoryRateLimiterStore();
            var forwarded = 0;
            var middleware = new RateLimitMiddleware(_ => { forwarded++; return Task.CompletedTask; }, NullLogger<RateLimitMiddleware>.Instance,
                Options.Create(new GatewayMiddlewareOptions()), null!, store,
                new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance));
            async Task<int> Request(string id, string ip = "127.0.0.1")
            {
                var context = new DefaultHttpContext(); context.Request.Path = "/" + id; context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                context.Features.Set<IReverseProxyFeature>(new ReverseProxyFeature { Route = new RouteModel(routes.Single(r => r.RouteId == id), null!, HttpTransformer.Default) });
                await using var body = new MemoryStream(); context.Response.Body = body;
                await middleware.InvokeAsync(context);
                if (context.Response.StatusCode == 429) Check(context.Response.Headers.RetryAfter.Count == 1, "Shared rejection lost retry guidance.");
                return context.Response.StatusCode;
            }
            Check(await Request("a") == 200 && await Request("b") == 200 && await Request("a") == 429 && await Request("b") == 429, "Routes did not share one allowance.");
            Check(await Request("c") == 200 && await Request("d") == 200 && await Request("e") == 200, "Independent groups or routes shared allowances.");
            Check(forwarded == 5 && store.Count == 4, "Shared group retained duplicate partitions or forwarded rejected traffic.");
            if (partition == "IpAddress") Check(await Request("b", "127.0.0.2") == 200, "Different client partition consumed another allowance.");
        }
    }
    private static async Task ValidationAsync()
    {
        var schema = new RateLimitPlugin().Manifest.Schemas.Single().ConfigJsonSchema;
        var validator = new PluginConfigurationSchemaValidator();
        foreach (var group in new[] { "", "inventory-v1", "Orders.Api_1" })
            Check(validator.TryValidate(Binding("a", group).ConfigJson, schema, out _, out _), "Valid group was rejected.");
        foreach (var group in new[] { " spaces ", "a:b", new string('a', 65), "_prefix" })
        {
            var binding = Binding("a", group);
            Check(!validator.TryValidate(binding.ConfigJson, schema, out _, out _), "Unsafe group name passed schema validation.");
            try { await new GatewaySnapshotCompiler(new InMemoryPluginRepository([binding])).CompileAsync([Route("a")], [], 1); throw new Exception("Unsafe group reached snapshot."); }
            catch (ArgumentException) { }
        }
        await using var host = await ConfigurationTransactionChecks.Fixture.Create();
        var update = await host.Config.ImportBatchAsync([new ClusterConfig { ClusterId = "backend", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://127.0.0.1:9/" } } }], [Route("a"), Route("b")]);
        Check(update.Success, "Quota fixture routes were not created.");
        var mutations = host.App.Services.GetRequiredService<IPluginBindingMutationService>();
        var repository = host.App.Services.GetRequiredService<IPluginConfigurationRepository>();
        await mutations.UpsertAsync(Binding("a", "inventory"));
        var before = await repository.GetBindingsSnapshotAsync(); var version = host.Publisher.Current.Version;
        try { await mutations.UpsertAsync(Binding("b", "inventory", 3)); throw new Exception("Mismatched group was committed."); }
        catch (ArgumentException) { }
        var after = await repository.GetBindingsSnapshotAsync();
        Check(before.Revision == after.Revision && after.Bindings.Count == 1 && host.Publisher.Current.Version == version, "Rejected group changed storage or active state.");
        await mutations.UpsertAsync(Binding("b", "inventory"));
        Check((await repository.GetBindingsSnapshotAsync()).Bindings.Count == 2, "Matching group could not be committed.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
