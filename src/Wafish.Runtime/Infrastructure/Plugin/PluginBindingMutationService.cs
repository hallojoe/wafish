using Wafish.Services;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Wafish.Plugins;

namespace Wafish.Dashboard.Infrastructure.Plugin;

public interface IPluginBindingMutationService
{
    Task UpsertAsync(PluginBindingEntity candidate, CancellationToken cancellationToken = default);
    Task ReconcileAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string bindingId, CancellationToken cancellationToken = default);
}

public sealed class PluginBindingMutationService : IPluginBindingMutationService
{
    private SemaphoreSlim PublishGate => GatewayPublicationCoordinator.For(_snapshotPublisher).Gate;
    private readonly WafishProxyConfigProvider? _configProvider;

    private readonly IConfigChangeAuditLog? _audit;
    private readonly IPluginConfigurationRepository _repository;
    private readonly IGatewayPluginManager _pluginManager;
    private readonly IDynamicYarpConfigService _dynamicConfig;
    private readonly IGatewaySnapshotCompiler _snapshotCompiler;
    private readonly IGatewaySnapshotPublisher _snapshotPublisher;
    private readonly IPluginRuntimeDomainManager _runtimeDomains;

    public PluginBindingMutationService(
        IPluginConfigurationRepository repository,
        IGatewayPluginManager pluginManager,
        IDynamicYarpConfigService dynamicConfig,
        IGatewaySnapshotCompiler snapshotCompiler,
        IGatewaySnapshotPublisher snapshotPublisher,
        IPluginRuntimeDomainManager runtimeDomains, WafishProxyConfigProvider? configProvider = null)
    {
        _configProvider = configProvider;
        _repository = repository;
        _pluginManager = pluginManager;
        _dynamicConfig = dynamicConfig;
        _snapshotCompiler = snapshotCompiler;
        _snapshotPublisher = snapshotPublisher;
        _runtimeDomains = runtimeDomains;
    }

    public PluginBindingMutationService(IPluginConfigurationRepository repository, IGatewayPluginManager pluginManager,
        IDynamicYarpConfigService dynamicConfig, IGatewaySnapshotCompiler snapshotCompiler, IGatewaySnapshotPublisher snapshotPublisher,
        IPluginRuntimeDomainManager runtimeDomains, WafishProxyConfigProvider? configProvider, IConfigChangeAuditLog audit)
        : this(repository, pluginManager, dynamicConfig, snapshotCompiler, snapshotPublisher, runtimeDomains, configProvider) => _audit = audit;

    private void AuditExclusions(PluginBindingEntity? before, PluginBindingEntity? after)
    {
        if (_audit is null) return;
        static Wafish.Models.WafRuleExclusion[] Fields(PluginBindingEntity? binding) => binding?.PluginId == "waf"
            ? System.Text.Json.JsonSerializer.Deserialize<Wafish.Models.WafBindingOptions>(binding.ConfigJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Exclusions.ToArray() : [];
        var previous = Fields(before); var current = Fields(after);
        if (previous.Length == 0 && current.Length == 0) return;
        if (previous.SequenceEqual(current) && before?.Enabled == after?.Enabled && before?.ScopeId == after?.ScopeId) return;
        _audit.RecordSuccess("WafExclusions", (after ?? before)!.Id, "plugin-binding-service",
            before: new { before?.ScopeId, before?.Enabled, exclusions = previous },
            after: new { after?.ScopeId, after?.Enabled, exclusions = current });
    }

    public async Task UpsertAsync(PluginBindingEntity candidate, CancellationToken cancellationToken = default)
    {
        await PublishGate.WaitAsync(cancellationToken);
        try
        {
            var stored = await _repository.GetBindingsSnapshotAsync(cancellationToken);
            var previous = stored.Bindings.FirstOrDefault(binding => binding.Id == candidate.Id);
            var bindings = stored.Bindings
                .Where(binding => binding.Id != candidate.Id)
                .Append(candidate)
                .ToArray();
            var snapshot = await CompileCandidateAsync(bindings, cancellationToken);
            await using var preparation = await PrepareRuntimeAsync(bindings, cancellationToken);

            var committedRevision = await _repository.CommitBindingsAsync(stored.Revision, [candidate], [], cancellationToken);
            try
            {
                await preparation.CommitAsync(CancellationToken.None);
                _snapshotPublisher.Publish(snapshot);
                GatewayPublicationCoordinator.For(_snapshotPublisher).Published(snapshot);
                GatewayPublicationCoordinator.For(_snapshotPublisher).ActiveBindingRevision = committedRevision;
                _configProvider?.Update(snapshot.Routes, snapshot.Clusters);
            }
            catch
            {
                if (previous == null)
                {
                    await _repository.CommitBindingsAsync(committedRevision, [], [candidate.Id], CancellationToken.None);
                }
                else
                {
                    await _repository.CommitBindingsAsync(committedRevision, [previous], [], CancellationToken.None);
                }

                throw;
            }

            AuditExclusions(previous, candidate);
        }
        finally
        {
            PublishGate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string bindingId, CancellationToken cancellationToken = default)
    {
        await PublishGate.WaitAsync(cancellationToken);
        try
        {
            var stored = await _repository.GetBindingsSnapshotAsync(cancellationToken);
            var existing = stored.Bindings.FirstOrDefault(binding => binding.Id == bindingId);
            if (existing == null)
            {
                return false;
            }

            var bindings = stored.Bindings
                .Where(binding => binding.Id != bindingId)
                .ToArray();
            var snapshot = await CompileCandidateAsync(bindings, cancellationToken);
            await using var preparation = await PrepareRuntimeAsync(bindings, cancellationToken);

            var committedRevision = await _repository.CommitBindingsAsync(stored.Revision, [], [bindingId], cancellationToken);

            try
            {
                await preparation.CommitAsync(CancellationToken.None);
                _snapshotPublisher.Publish(snapshot);
                GatewayPublicationCoordinator.For(_snapshotPublisher).Published(snapshot);
                GatewayPublicationCoordinator.For(_snapshotPublisher).ActiveBindingRevision = committedRevision;
                _configProvider?.Update(snapshot.Routes, snapshot.Clusters);
            }
            catch
            {
                await _repository.CommitBindingsAsync(committedRevision, [existing], [], CancellationToken.None);
                throw;
            }

            AuditExclusions(existing, null);
            return true;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await PublishGate.WaitAsync(cancellationToken);
        try
        {
            var stored = await _repository.GetBindingsSnapshotAsync(cancellationToken);
            var snapshot = await CompileCandidateAsync(stored.Bindings, cancellationToken);
            await using var preparation = await PrepareRuntimeAsync(stored.Bindings.ToArray(), cancellationToken);
            await preparation.CommitAsync(CancellationToken.None);
            _snapshotPublisher.Publish(snapshot);
            GatewayPublicationCoordinator.For(_snapshotPublisher).Published(snapshot);
            GatewayPublicationCoordinator.For(_snapshotPublisher).ActiveBindingRevision = stored.Revision;
            _configProvider?.Update(snapshot.Routes, snapshot.Clusters);
        }
        finally { PublishGate.Release(); }
    }

    private async Task<GatewaySnapshot> CompileCandidateAsync(
        IReadOnlyList<PluginBindingEntity> bindings, CancellationToken cancellationToken)
    {
        var stored = _dynamicConfig.GetDynamicConfig();
        var destinations = stored?.Clusters.ToDictionary(c => c.Config.ClusterId, c => c.Config.Destinations);
        var clusters = _dynamicConfig.GetClusters().Select(cluster => cluster with
        {
            Destinations = destinations?.GetValueOrDefault(cluster.ClusterId) ?? cluster.Destinations
        }).ToArray();
        var snapshot = await _snapshotCompiler.CompileAsync(_snapshotPublisher.Current.Routes, clusters,
            _snapshotPublisher.Current.Version + 1, cancellationToken, bindings);
        return GatewayPublicationCoordinator.For(_snapshotPublisher).PreserveDiscovery(snapshot);
    }

    private async Task<PluginRuntimeDomainPreparation> PrepareRuntimeAsync(
        IReadOnlyCollection<PluginBindingEntity> bindings,
        CancellationToken cancellationToken)
    {
        var enabledPluginIds = bindings.Where(binding => binding.Enabled).Select(binding => binding.PluginId)
            .Concat(_pluginManager.GetAllManifests()
                .Where(manifest => _pluginManager.IsPluginEnabled(manifest.Id))
                .Select(manifest => manifest.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var preparation = await _runtimeDomains.PrepareAsync(enabledPluginIds, cancellationToken);
        var health = await preparation.CheckHealthAsync(cancellationToken);
        if (health.Status == PluginHealthStatus.Unhealthy)
        {
            await preparation.DisposeAsync();
            throw new InvalidOperationException(health.Message ?? "Candidate plugin runtime domain is unhealthy.");
        }

        return preparation;
    }
}
