using Wafish.Models;
using Wafish.Storage.Entities;

namespace Wafish.Services;

/// <summary>Complete desired configuration and binding sets, protected by independent revisions.</summary>
public sealed record ConfigurationChangeSetRequest(ConfigurationChangeRequest Configuration,
    long ExpectedBindingRevision, IReadOnlyList<PluginBindingEntity> Bindings);

public sealed record ConfigurationChangeSetPreview(bool Valid, long ActiveVersion, long PersistedVersion,
    long? ActiveBindingRevision, long PersistedBindingRevision, string Actor,
    ConfigurationChangeSetRequest? Normalized, IReadOnlyList<ConfigurationChange> Changes, IReadOnlyList<string> Errors)
{
    public bool Conflict { get; init; }
}

public sealed record ConfigurationChangeSetResult(RouteOperationResult Configuration, long? ActiveBindingRevision,
    long? PersistedBindingRevision, string Actor, string CorrelationId);

public interface IConfigurationChangeSetService
{
    Task<ConfigurationChangeSetPreview> PreviewChangeSetAsync(ConfigurationChangeSetRequest request, string actor, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeSetResult> ApplyChangeSetAsync(ConfigurationChangeSetRequest request, string actor, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>Runtime validation and preparation without a dependency from core to the runtime assembly.</summary>
public interface IConfigurationChangeSetRuntime
{
    void Validate(IReadOnlyList<PluginBindingEntity> bindings);
    Task<IConfigurationChangeSetPreparation> PrepareAsync(IReadOnlyList<PluginBindingEntity> bindings, CancellationToken cancellationToken);
}

public interface IConfigurationChangeSetPreparation : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
