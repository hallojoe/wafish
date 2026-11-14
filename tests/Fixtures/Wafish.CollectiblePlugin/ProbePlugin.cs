using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Plugins;

namespace Wafish.RegressionFixtures;

public sealed class ProbePlugin : IGatewayPlugin, IPluginRuntimeResource, IAsyncDisposable
{
    private Action<string>? _notify;
    private Task _stop = Task.CompletedTask;
    private bool _fail;
    private OwnedResource? _resource;
    public string PluginId => "collectible-probe";
    public string DisplayName => "Collectible regression fixture";
    public string Version => "1.0.0";
    public string ResourceId => PluginId;
    public string ResourceType => "fixture";
    public PluginManifest Manifest => new(PluginId, DisplayName, Version, [], [], 0, new(), []);
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null)
    {
        if (pluginOptions is Tuple<Action<string>, Task, bool> options) { _notify = options.Item1; _stop = options.Item2; _fail = options.Item3; }
        services.AddSingleton<OwnedResource>();
    }
    public void ConfigureMiddleware(IApplicationBuilder app) => _resource = app.ApplicationServices.GetRequiredService<OwnedResource>();
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    public ValueTask StartResourceAsync(CancellationToken cancellationToken) { _notify?.Invoke("start"); return ValueTask.CompletedTask; }
    public async ValueTask StopResourceAsync(CancellationToken cancellationToken)
    {
        _notify?.Invoke("stop-start");
        await _stop.WaitAsync(cancellationToken);
        if (_resource?.Disposed == true) throw new FixtureFailure("Provider disposed before resource shutdown.");
        _notify?.Invoke("stop-end");
        if (_fail) throw new FixtureFailure("Injected stop failure.");
    }
    public ValueTask<PluginRuntimeResourceSnapshot> CheckHealthAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PluginRuntimeResourceSnapshot(ResourceId, ResourceType, true, PluginResourceHealthStatus.Healthy, null, null, null, new Dictionary<string, long>()));
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _notify?.Invoke("dispose");
        _resource = null;
        _notify = null;
        if (_fail) throw new FixtureFailure("Injected disposal failure.");
    }
    public sealed class OwnedResource : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
    private sealed class FixtureFailure(string message) : Exception(message);
}

public sealed class SyncProbePlugin : IGatewayPlugin, IDisposable
{
    private Action<string>? _notify;
    public string PluginId => "collectible-sync";
    public string DisplayName => "Synchronous collectible fixture";
    public string Version => "1.0.0";
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) => _notify = pluginOptions as Action<string>;
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    public void Dispose() { _notify?.Invoke("dispose"); _notify = null; }
}
