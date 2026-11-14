using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugins;

internal static class PluginManifestPolicyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Plugin policy: constraints preserve built-in ordering and reject missing targets and cycles", () =>
        {
            var cache = Manifest("response-cache", 999);
            var retry = Manifest("request-retry", 9999);
            var compression = Manifest("compression", -10);
            var extra = Manifest("external", 0) with { Before = ["compression"], After = ["response-cache"] };
            var order = PluginManifestPolicy.Resolve([compression, extra, cache, retry]).Select(m => m.Id);
            Require(order.SequenceEqual(new[] { "request-retry", "response-cache", "external", "compression" }), "Pipeline order changed.");
            Reject([extra]);
            Reject([cache with { Before = ["request-retry"] }, retry]);
            Reject([Manifest("one") with { Before = ["two"] }, Manifest("two") with { Before = ["one"] }]);
            Reject([Manifest("one") with { RequiredHostCapabilities = [new("unknown")] }]);
            Reject([Manifest("one") with { RequiredHostCapabilities = [new("wafish.runtime", 2)] }]);
            Reject([Manifest("one") with { RequiredHostCapabilities = [new("wafish.runtime", 0)] }]);
            Require(PluginManifestPolicy.Resolve([Manifest("one") with { RequiredHostCapabilities = [new("wafish.runtime")] }]).Count == 1, "Supported capability rejected.");
            return Task.CompletedTask;
        });
        await run("Plugin policy: invalid replacements preserve the active domain before services are created", async () =>
        {
            var calls = new List<string>();
            var first = new Plugin(Manifest("first"), calls);
            var second = new Plugin(Manifest("second") with { After = ["first"] }, calls);
            await using var root = new ServiceCollection().AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Directory.GetCurrentDirectory()))
                .AddSingleton<IGatewayPlugin>(first).AddSingleton<IGatewayPlugin>(second).BuildServiceProvider();
            await using var host = new ExternalGatewayPluginHost(root.GetRequiredService<IHostEnvironment>(), root.GetRequiredService<IConfiguration>(), NullLogger<ExternalGatewayPluginHost>.Instance);
            await using var manager = new PluginRuntimeDomainManager(root, host, NullLogger<PluginRuntimeDomainManager>.Instance);
            Require((await manager.TransitionAsync(["first", "second"])).Succeeded, "Valid transition failed.");
            var active = manager.Current;
            await manager.InvokeProxyPipelineAsync(new DefaultHttpContext(), _ => Task.CompletedTask);
            Require(calls.SequenceEqual(new[] { "first", "second" }), "External pipeline callbacks did not execute in order.");
            first.Metadata = first.Metadata with { RequiredHostCapabilities = [new("unsupported")] };
            Require(!(await manager.TransitionAsync(["first", "second"])).Succeeded, "Unsupported replacement activated.");
            Require(ReferenceEquals(active, manager.Current) && first.Configurations == 1, "Validation created resources or replaced active domain.");
            first.Metadata = first.Metadata with { RequiredHostCapabilities = [], Before = ["absent"] };
            Require(!(await manager.TransitionAsync(["first", "second"])).Succeeded && ReferenceEquals(active, manager.Current), "Missing target replaced active domain.");
        });
    }
    private static PluginManifest Manifest(string id, int order = 0) => new(id, id, "1.0", [PluginScope.Route], [], order, new(), []);
    private static void Reject(PluginManifest[] manifests)
    {
        try { PluginManifestPolicy.Resolve(manifests); throw new Exception("Invalid manifest policy accepted."); }
        catch (InvalidOperationException) { }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Plugin(PluginManifest manifest, List<string> calls) : IGatewayPlugin
    {
        public PluginManifest Metadata = manifest;
        public int Configurations;
        public string PluginId => Metadata.Id;
        public string DisplayName => Metadata.Name;
        public string Version => Metadata.Version;
        public PluginManifest Manifest => Metadata;
        public void ConfigureServices(IServiceCollection services, object? options = null) => Configurations++;
        public void ConfigureMiddleware(IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder app) => app.Use(async (context, next) => { calls.Add(PluginId); await next(context); });
        public void ConfigureDashboard(IPluginDashboardBuilder builder) { }
    }
}
