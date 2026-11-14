using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugins;

internal static class PluginPreviewChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Plugin preview: candidate health and requirements leave active traffic unchanged and release services", async () =>
        {
            var plugin = new ProbePlugin();
            await using var root = Root(plugin);
            await using var host = Host(root);
            await using var manager = new PluginRuntimeDomainManager(root, host, NullLogger<PluginRuntimeDomainManager>.Instance);
            Require((await manager.TransitionAsync(["probe"])).Succeeded, "Initial activation failed.");
            var active = manager.Current;
            var report = await manager.PreviewAsync(["probe"]);
            Require(report.Ready && report.Plugins.Single().Resources.NetworkConnections && report.Plugins.Single().Health?.Status == PluginHealthStatus.Healthy, "Preview omitted requirements or health.");
            Require(ReferenceEquals(active, manager.Current) && plugin.Created == 2 && plugin.Disposed == 1, "Preview replaced active domain or leaked candidate services.");
            await manager.InvokeMiddlewareAsync(new DefaultHttpContext(), _ => Task.CompletedTask);
            Require(plugin.Requests == 1, "Preview interrupted active traffic.");
            plugin.Health = PluginHealthStatus.Unhealthy;
            report = await manager.PreviewAsync(["probe"]);
            Require(!report.Ready && report.Errors.Single().Contains("probe") && plugin.Disposed == 2 && ReferenceEquals(active, manager.Current), "Failed health activated or leaked candidate.");
            plugin.Health = PluginHealthStatus.Disabled;
            Require(!(await manager.PreviewAsync(["probe"])).Ready, "Disabled probe reported ready.");
            plugin.Throw = true;
            Require(!(await manager.PreviewAsync(["probe"])).Ready && plugin.Disposed == 4, "Throwing probe leaked candidate.");
        });
        await run("Plugin preview: cancellation disposes candidates and missing plugins never report success", async () =>
        {
            var plugin = new ProbePlugin();
            await using var root = Root(plugin);
            await using var host = Host(root);
            await using var manager = new PluginRuntimeDomainManager(root, host, NullLogger<PluginRuntimeDomainManager>.Instance);
            var active = manager.Current;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            plugin.Probe = async ct => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
            using var cancellation = new CancellationTokenSource();
            var pending = manager.PreviewAsync(["probe"], cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { await pending; throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
            Require(plugin.Disposed == 1 && ReferenceEquals(active, manager.Current), "Cancellation leaked or activated candidate.");
            Require(!(await manager.PreviewAsync(["missing"])).Ready, "Missing plugin reported ready.");
            Require(!(await manager.PreviewAsync(["probe", "PROBE"])).Ready, "Duplicate IDs accepted.");
            Require(!(await manager.PreviewAsync([""])).Ready, "Empty ID accepted.");
            plugin.Probe = null;
            plugin.Metadata = plugin.Metadata with { RequiredHostCapabilities = [new("unsupported")] };
            Require(!(await manager.PreviewAsync(["probe"])).Ready && plugin.Created == 1, "Invalid policy constructed resources.");
            Require((await manager.PreviewAsync([])).Ready, "Empty candidate rejected.");
        });
        await run("Plugin preview: plugins without health probes report unknown health", async () =>
        {
            await using var root = Root(new PlainPlugin());
            await using var host = Host(root);
            await using var manager = new PluginRuntimeDomainManager(root, host, NullLogger<PluginRuntimeDomainManager>.Instance);
            var report = await manager.PreviewAsync(["plain"]);
            Require(report.Ready && report.Plugins.Single().Health is null, "Missing health probe was reported healthy.");
        });
    }
    private static ServiceProvider Root(IGatewayPlugin plugin) => new ServiceCollection().AddLogging()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
        .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Directory.GetCurrentDirectory()))
        .AddSingleton<IGatewayPlugin>(plugin).BuildServiceProvider();
    private static ExternalGatewayPluginHost Host(IServiceProvider root) => new(root.GetRequiredService<IHostEnvironment>(), root.GetRequiredService<IConfiguration>(), NullLogger<ExternalGatewayPluginHost>.Instance);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private class PlainPlugin : IGatewayPlugin
    {
        public virtual string PluginId => "plain";
        public string DisplayName => PluginId;
        public string Version => "1.0";
        public virtual void ConfigureServices(IServiceCollection services, object? options = null) { }
        public virtual void ConfigureMiddleware(IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder app) { }
        public void ConfigureDashboard(IPluginDashboardBuilder builder) { }
    }
    private sealed class ProbePlugin : PlainPlugin, IGatewayPlugin, IPluginHealthProbe
    {
        public override string PluginId => "probe";
        public PluginManifest Metadata = new("probe", "probe", "1.0", [], [], 0, new(NetworkConnections: true), []);
        public PluginManifest Manifest => Metadata;
        public int Created, Disposed, Requests;
        public PluginHealthStatus Health = PluginHealthStatus.Healthy;
        public bool Throw;
        public Func<CancellationToken, Task>? Probe;
        public override void ConfigureServices(IServiceCollection services, object? options = null) => services.AddSingleton(_ => new Owned(this));
        public override void ConfigureMiddleware(IApplicationBuilder app)
        {
            _ = app.ApplicationServices.GetRequiredService<Owned>();
            app.Use(async (context, next) => { Requests++; await next(context); });
        }
        public async ValueTask<PluginHealthProbeResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            if (Probe is not null) await Probe(cancellationToken);
            if (Throw) throw new InvalidOperationException("Injected health failure.");
            return new(Health, "fixture", DateTimeOffset.UtcNow);
        }
    }
    private sealed class Owned : IDisposable
    {
        private readonly ProbePlugin _owner;
        public Owned(ProbePlugin owner) { _owner = owner; owner.Created++; }
        public void Dispose() => _owner.Disposed++;
    }
}
