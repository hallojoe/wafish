using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wafish.Models;
using Wafish.Services.ProxyConfigHealth;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

public partial class DynamicYarpConfigService : IConfigurationChangeSetService
{
    private readonly IPluginConfigurationRepository? _changeSetBindings;
    private readonly IConfigurationChangeSetRuntime? _changeSetRuntime;
    private GatewayPublicationCoordinator Coordinator => GatewayPublicationCoordinator.For(_snapshotPublisher);

    public DynamicYarpConfigService(WafishProxyConfigProvider configProvider, IConfigurationSnapshotRepository snapshots,
        IConfigChangeAuditLog auditLog, ILoggerFactory loggerFactory, IGatewaySnapshotCompiler snapshotCompiler,
        IGatewaySnapshotPublisher snapshotPublisher, IConfigValidator configValidator, IProxyConfigErrorStore errorStore,
        TimeProvider timeProvider, IPluginConfigurationRepository bindings, IConfigurationChangeSetRuntime runtime)
        : this(configProvider, snapshots, auditLog, loggerFactory, snapshotCompiler, snapshotPublisher, configValidator, errorStore, timeProvider)
    { _changeSetBindings = bindings; _changeSetRuntime = runtime; }

    public async Task<ConfigurationChangeSetPreview> PreviewChangeSetAsync(ConfigurationChangeSetRequest request, string actor, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        long revision = 0;
        try
        {
            RequireChangeSetSupport();
            request = JsonSerializer.Deserialize<ConfigurationChangeSetRequest>(JsonSerializer.Serialize(request))!;
            var stored = await _changeSetBindings!.GetBindingsSnapshotAsync(cancellationToken);
            revision = stored.Revision;
            var (candidate, normalized) = NormalizeChangeSet(request, stored);
            await _publisher.PrepareChangeSetAsync(candidate, normalized.Bindings, cancellationToken);
            return new(true, _state.Version, _state.PersistedVersion, Coordinator.ActiveBindingRevision, revision, actor,
                normalized, ChangeSetDiff(candidate, stored.Bindings, normalized.Bindings), []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ConfigurationVersionConflictException or JsonException or NotSupportedException)
        {
            return new(false, _state.Version, _state.PersistedVersion, Coordinator.ActiveBindingRevision, revision, actor, null, [], [error.Message])
            { Conflict = error is ConfigurationVersionConflictException or BindingRevisionConflictException };
        }
        finally { _semaphore.Release(); }
    }

    public async Task<ConfigurationChangeSetResult> ApplyChangeSetAsync(ConfigurationChangeSetRequest request, string actor, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 124 || idempotencyKey.Any(char.IsControl))
            return Result(ConfigurationUpdateStatus.ValidationRejected, "A printable idempotency key of 1–124 characters is required.");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || actor.Any(char.IsControl))
            return Result(ConfigurationUpdateStatus.ValidationRejected, "A bounded actor name is required.");
        var key = "set:" + idempotencyKey;
        await _semaphore.WaitAsync(cancellationToken);
        long? storedRevision = null;
        try
        {
            try
            {
                RequireChangeSetSupport();
                request = JsonSerializer.Deserialize<ConfigurationChangeSetRequest>(JsonSerializer.Serialize(request))!;
                var fingerprint = Fingerprint(new { Kind = "change-set", Actor = actor, Request = request });
                var repository = (IConfigurationChangeSetRepository)_snapshotRepository;
                if (await repository.GetReceiptAsync(key, cancellationToken) is { } receipt) return ReplaySet(receipt, fingerprint);
                var stored = await _changeSetBindings!.GetBindingsSnapshotAsync(cancellationToken);
                storedRevision = stored.Revision;
                var (candidate, normalized) = NormalizeChangeSet(request, stored);
                var activate = await _publisher.PrepareChangeSetAsync(candidate, normalized.Bindings, cancellationToken);
                await using var runtime = await _changeSetRuntime!.PrepareAsync(normalized.Bindings, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Storage, activation, audit, and receipt completion retain ownership after commit starts.
                    storedRevision = await repository.SaveChangeSetAsync(DynamicConfigPersister.ToSnapshot(candidate),
                        request.Configuration.ExpectedVersion, request.ExpectedBindingRevision, normalized.Bindings, key, fingerprint, CancellationToken.None);
                }
                catch (ConfigurationReceiptExistsException)
                {
                    var previous = await repository.GetReceiptAsync(key, CancellationToken.None);
                    return previous is null ? Result(ConfigurationUpdateStatus.VersionConflict, "Receipt unavailable. Inspect current revisions.") : ReplaySet(previous, fingerprint);
                }
                catch (ConfigurationVersionConflictException) { throw; }
                catch (BindingRevisionConflictException) { throw; }
                catch (Exception error)
                {
                    _state.LogFailure?.Invoke(error, "ApplyChangeSet");
                    return Audit(Result(ConfigurationUpdateStatus.StorageFailed, "Change set storage failed. No candidate was activated."));
                }
                _state.PersistedVersion = candidate.Version;
                ConfigurationChangeSetResult result;
                try
                {
                    await runtime.CommitAsync(CancellationToken.None);
                    activate();
                    _state.Config = candidate;
                    Coordinator.ActiveBindingRevision = storedRevision;
                    result = Result(ConfigurationUpdateStatus.Applied, "Change set applied.", storedRevision);
                }
                catch (Exception error)
                {
                    _state.LogFailure?.Invoke(error, "ActivateChangeSet");
                    result = Result(ConfigurationUpdateStatus.ActivationFailed, "Change set was stored but activation failed. Restart to reconcile.", storedRevision);
                }
                Audit(result);
                try { await repository.CompleteReceiptAsync(key, JsonSerializer.Serialize(result), CancellationToken.None); }
                catch (Exception error)
                {
                    _state.LogFailure?.Invoke(error, "CompleteChangeSetReceipt");
                    return PendingSet(candidate.Version, storedRevision);
                }
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ConfigurationVersionConflictException error)
            { return Audit(Result(ConfigurationUpdateStatus.VersionConflict, error.Message, storedRevision) with { Configuration = ChangeFailure(ConfigurationUpdateStatus.VersionConflict, error.Message) with { PersistedVersion = error.ActualVersion } }); }
            catch (BindingRevisionConflictException error)
            { return Audit(Result(ConfigurationUpdateStatus.VersionConflict, error.Message, error.ActualRevision)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
            { return Audit(Result(ConfigurationUpdateStatus.ValidationRejected, error.Message, storedRevision)); }
        }
        finally { _semaphore.Release(); }

        ConfigurationChangeSetResult Result(ConfigurationUpdateStatus status, string message, long? revision = null)
            => new(new(status == ConfigurationUpdateStatus.Applied, message) { Status = status, ActiveVersion = _state.Version, PersistedVersion = _state.PersistedVersion },
                Coordinator.ActiveBindingRevision, revision, actor, idempotencyKey);
        ConfigurationChangeSetResult PendingSet(long version, long? revision)
            => new(PendingReceipt(version), null, revision, actor, idempotencyKey);
        ConfigurationChangeSetResult ReplaySet(ConfigurationMutationReceipt receipt, string fingerprint)
            => receipt.Fingerprint != fingerprint ? Result(ConfigurationUpdateStatus.VersionConflict, "Idempotency key was used for another change set.")
                : receipt.ResultJson is null ? PendingSet(receipt.Version, null) : JsonSerializer.Deserialize<ConfigurationChangeSetResult>(receipt.ResultJson)!;
        ConfigurationChangeSetResult Audit(ConfigurationChangeSetResult result)
        {
            try
            {
                _auditLog.Record(new ConfigChangeAudit
                {
                    Action = "ApplyChangeSet", Target = idempotencyKey, Operator = actor, Success = result.Configuration.Success,
                    ErrorMessage = result.Configuration.Success ? null : result.Configuration.Status.ToString(),
                    After = JsonSerializer.Serialize(new { result.Configuration.Status, result.Configuration.ActiveVersion,
                        result.Configuration.PersistedVersion, result.ActiveBindingRevision, result.PersistedBindingRevision }),
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception error) { _state.LogFailure?.Invoke(error, "AuditChangeSet"); }
            return result;
        }
    }

    private void RequireChangeSetSupport()
    {
        if (_snapshotRepository is not IConfigurationChangeSetRepository || _changeSetBindings is null || _changeSetRuntime is null)
            throw new NotSupportedException("Atomic change sets require runtime support and an atomic storage provider.");
    }

    private (GatewayDynamicConfig Candidate, ConfigurationChangeSetRequest Normalized) NormalizeChangeSet(ConfigurationChangeSetRequest request, PluginBindingsSnapshot stored)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Configuration);
        CheckChangeVersion(request.Configuration);
        if (request.ExpectedBindingRevision != stored.Revision) throw new BindingRevisionConflictException(request.ExpectedBindingRevision, stored.Revision);
        if (request.Bindings is null || request.Bindings.Count > 10000) throw new ArgumentException("Supply at most 10,000 bindings.");
        var bindings = JsonSerializer.Deserialize<PluginBindingEntity[]>(JsonSerializer.Serialize(request.Bindings))!;
        if (bindings.Any(b => b is null || string.IsNullOrWhiteSpace(b.Id) || b.Id.Length > 256 || !Enum.IsDefined(b.Scope)
            || string.IsNullOrWhiteSpace(b.PluginId) || b.ConfigJson is null || b.ConfigJson.Length > 65536))
            throw new ArgumentException("Binding identity, scope, or configuration size is invalid.");
        if (bindings.Select(b => b.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bindings.Length)
            throw new ArgumentException("Binding IDs must be unique.");
        if (bindings.GroupBy(b => (b.Scope, b.ScopeId, b.PluginId)).Any(g => g.Count() > 1))
            throw new ArgumentException("Only one binding per plugin and target is permitted.");
        var candidate = BuildCandidate(request.Configuration);
        foreach (var binding in bindings)
        {
            // ScopeId addresses candidate names, including newly created targets. Server-owned UIDs cannot be supplied.
            binding.RouteUid = binding.Scope == PluginBindingScope.Route
                ? candidate.Routes.SingleOrDefault(r => r.Config.RouteId == binding.ScopeId)?.RouteUid ?? throw new ArgumentException("Binding route does not exist.") : null;
            binding.ClusterUid = binding.Scope == PluginBindingScope.Cluster
                ? candidate.Clusters.SingleOrDefault(c => c.Config.ClusterId == binding.ScopeId)?.ClusterUid ?? throw new ArgumentException("Binding cluster does not exist.") : null;
            var previous = stored.Bindings.SingleOrDefault(b => b.Id == binding.Id);
            binding.ConfigVersion = previous is null ? 1 : checked(previous.ConfigVersion + 1);
            binding.CreatedAt = previous?.CreatedAt ?? DateTime.UtcNow;
            binding.UpdatedAt = DateTime.UtcNow;
        }
        _changeSetRuntime!.Validate(bindings);
        var configuration = new ConfigurationChangeRequest(request.Configuration.ExpectedVersion, candidate.Routes.Select(r => r.Config).ToArray(), candidate.Clusters.Select(c => c.Config).ToArray())
        { RouteActivation = candidate.Routes.ToDictionary(r => r.Config.RouteId, r => r.Enabled) };
        return (candidate, new(configuration, request.ExpectedBindingRevision, bindings));
    }

    private ConfigurationChange[] ChangeSetDiff(GatewayDynamicConfig candidate, IReadOnlyList<PluginBindingEntity> previous, IReadOnlyList<PluginBindingEntity> next)
        => Diff(_state.Config.Routes.Select(r => (r.Config.RouteId, Fingerprint(new { r.Config, r.Enabled }))), candidate.Routes.Select(r => (r.Config.RouteId, Fingerprint(new { r.Config, r.Enabled }))), "route")
            .Concat(Diff(_state.Config.Clusters.Select(c => (c.Config.ClusterId, Fingerprint(c.Config))), candidate.Clusters.Select(c => (c.Config.ClusterId, Fingerprint(c.Config))), "cluster"))
            .Concat(Diff(previous.Select(b => (b.Id, BindingFingerprint(b))), next.Select(b => (b.Id, BindingFingerprint(b))), "binding")).ToArray();
    private static string BindingFingerprint(PluginBindingEntity b)
        => Fingerprint(new { b.PluginId, b.Scope, b.ScopeId, b.Enabled, Configuration = JsonSerializer.Deserialize<JsonElement>(b.ConfigJson), b.SchemaVersion, b.Order });
}
