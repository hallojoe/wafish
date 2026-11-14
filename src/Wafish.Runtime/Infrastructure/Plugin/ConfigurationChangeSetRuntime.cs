using Wafish.Plugins;
using Wafish.Services;
using Wafish.Storage.Entities;

namespace Wafish.Dashboard.Infrastructure.Plugin;

internal sealed class ConfigurationChangeSetRuntime(IGatewayPluginManager plugins, IPluginConfigurationSchemaValidator schemas,
    IPluginRuntimeDomainManager domains) : IConfigurationChangeSetRuntime
{
    public void Validate(IReadOnlyList<PluginBindingEntity> bindings)
    {
        foreach (var binding in bindings)
        {
            var manifest = plugins.GetManifest(binding.PluginId) ?? throw new ArgumentException("Binding plugin is not installed.");
            if (binding.Enabled && !plugins.IsPluginEnabled(binding.PluginId)) throw new ArgumentException("Binding plugin is disabled.");
            var scope = binding.Scope == PluginBindingScope.Route ? PluginScope.Route : PluginScope.Cluster;
            if (!manifest.Scopes.Contains(scope)) throw new ArgumentException("Binding scope is not supported by this plugin.");
            var schema = manifest.Schemas.SingleOrDefault(s => s.Version == binding.SchemaVersion)
                ?? throw new ArgumentException("Binding schema version is not supported.");
            if (!schemas.TryValidate(binding.ConfigJson, schema.ConfigJsonSchema, out var normalized, out var error))
                throw new ArgumentException("Binding configuration is invalid: " + error);
            binding.ConfigJson = normalized;
            binding.PluginVersion = manifest.Version;
        }
    }

    public async Task<IConfigurationChangeSetPreparation> PrepareAsync(IReadOnlyList<PluginBindingEntity> bindings, CancellationToken cancellationToken)
    {
        var ids = plugins.GetAllManifests().Where(m => plugins.IsPluginEnabled(m.Id)).Select(m => m.Id).ToArray();
        var preparation = await domains.PrepareAsync(ids, cancellationToken);
        try
        {
            var health = await preparation.CheckHealthAsync(cancellationToken);
            if (health.Status == PluginHealthStatus.Unhealthy) throw new InvalidOperationException("Candidate runtime is unhealthy.");
            return new Preparation(preparation);
        }
        catch { await preparation.DisposeAsync(); throw; }
    }
    private sealed class Preparation(PluginRuntimeDomainPreparation inner) : IConfigurationChangeSetPreparation
    {
        public Task CommitAsync(CancellationToken cancellationToken) => inner.CommitAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
