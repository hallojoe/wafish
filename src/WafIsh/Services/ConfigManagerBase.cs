using WafIsh.Models;
using Microsoft.Extensions.Logging;

namespace WafIsh.Services;

/// <summary>
/// Shared base class for <see cref="RouteConfigManager"/> and <see cref="ClusterConfigManager"/>.
/// Provides the common lock/publish/persist pattern so each CRUD method only needs to
/// implement its domain logic.
/// </summary>
internal abstract class ConfigManagerBase
{
    protected readonly DynamicConfigState State;
    protected readonly SemaphoreSlim Semaphore;
    protected readonly IDynamicConfigPersister Persister;
    protected readonly IDynamicConfigPublisher Publisher;
    protected readonly IConfigChangeAuditLog AuditLog;

    protected ConfigManagerBase(
        DynamicConfigState state,
        SemaphoreSlim semaphore,
        IDynamicConfigPersister persister,
        IDynamicConfigPublisher publisher,
        IConfigChangeAuditLog auditLog)
    {
        State = state;
        Semaphore = semaphore;
        Persister = persister;
        Publisher = publisher;
        AuditLog = auditLog;
    }

    /// <summary>
    /// Execute a CRUD operation under lock. The <paramref name="action"/> receives the mutable
    /// <see cref="GatewayDynamicConfig"/> and returns a result. On success the config is
    /// validated, persisted atomically, and then published to YARP.
    /// </summary>
    protected async Task<RouteOperationResult> ExecuteWithLockAsync(
        string operationName,
        string? targetName,
        Func<GatewayDynamicConfig, Task<RouteOperationResult>> action)
    {
        await Semaphore.WaitAsync();
        try
        {
            State.EnsureInitialized();
            using var mutation = new ConfigurationMutation(State, Persister, Publisher, (DeferredConfigChangeAuditLog)AuditLog);
            var result = await action(mutation.Config);
            return result.Success
                ? await mutation.CommitAsync(operationName, targetName, result.Message)
                : result with { ActiveVersion = State.Version, PersistedVersion = State.PersistedVersion };
        }
        finally { Semaphore.Release(); }
    }

    /// <summary>
    /// Execute a read-only operation under lock.
    /// </summary>
    protected async Task<T> ExecuteReadWithLockAsync<T>(Func<GatewayDynamicConfig, T> action)
    {
        await Semaphore.WaitAsync();
        try
        {
            State.EnsureInitialized();
            return action(State.Config);
        }
        finally { Semaphore.Release(); }
    }

    /// <summary>Overridden by subclasses to provide typed logger for persist errors.</summary>
    protected abstract void LogPersistError(Exception ex, string operationName, string? targetName);
}
