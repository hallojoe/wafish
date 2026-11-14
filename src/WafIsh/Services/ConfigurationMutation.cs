using WafIsh.Models;
using WafIsh.Storage;
namespace WafIsh.Services;

/// <summary>Caller holds the configuration lock for this scope.</summary>
internal sealed class ConfigurationMutation : IDisposable
{
    private readonly DynamicConfigState _state;
    private readonly IDynamicConfigPersister _persister;
    private readonly IDynamicConfigPublisher _publisher;
    private readonly DeferredConfigChangeAuditLog _audit;
    public GatewayDynamicConfig Config { get; private set; }

    public ConfigurationMutation(DynamicConfigState state, IDynamicConfigPersister persister,
        IDynamicConfigPublisher publisher, DeferredConfigChangeAuditLog audit)
    {
        _state = state;
        _persister = persister;
        _publisher = publisher;
        _audit = audit;
        Config = DynamicConfigState.Clone(state.Config);
        audit.Begin();
    }

    public async Task<RouteOperationResult> CommitAsync(string operation, string? target = null, string? message = null)
    {
        if (_state.Version != _state.PersistedVersion)
        {
            return Failure(ConfigurationUpdateStatus.ActivationFailed,
                "A stored revision is not active. Restart the host before another update.");
        }

        Config = DynamicConfigState.Clone(Config);
        Config.Version = _state.PersistedVersion + 1;
        Config.LastModified = DateTime.UtcNow;
        // Always repair references after implicit cluster creation or renaming.
        foreach (var route in Config.Routes)
        {
            route.ClusterUid = DynamicConfigState.ResolveClusterUid(Config, route.Config.ClusterId);
        }

        Action activate;
        try { activate = _publisher.Prepare(Config, Config.Version); }
        catch (Exception ex) { return Failure(ConfigurationUpdateStatus.ValidationRejected, ex.Message); }
        try { await _persister.SaveAsync(Config, operation, target); }
        catch (ConfigurationVersionConflictException ex)
        {
            return Failure(ConfigurationUpdateStatus.VersionConflict, ex.Message) with { PersistedVersion = ex.ActualVersion };
        }
        catch (Exception ex)
        {
            _state.LogFailure?.Invoke(ex, operation);
            return Failure(ConfigurationUpdateStatus.StorageFailed, "Configuration storage failed. The active configuration is unchanged.");
        }
        _state.PersistedVersion = Config.Version;
        try { activate(); }
        catch (Exception ex)
        {
            _state.LogFailure?.Invoke(ex, operation);
            return Failure(ConfigurationUpdateStatus.ActivationFailed,
                "Configuration was stored but activation failed. Restart the host to apply the stored revision.");
        }
        _state.Config = Config;
        _audit.Commit();
        return new(true, message ?? "Configuration applied")
        {
            Status = ConfigurationUpdateStatus.Applied,
            ActiveVersion = _state.Version,
            PersistedVersion = _state.PersistedVersion
        };
    }

    private RouteOperationResult Failure(ConfigurationUpdateStatus status, string message)
        => new(false, message) { Status = status, ActiveVersion = _state.Version, PersistedVersion = _state.PersistedVersion };

    public void Dispose() => _audit.Discard();
}
