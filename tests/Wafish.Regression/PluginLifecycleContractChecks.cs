using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugins;
using Wafish.Services;
using Wafish.Storage.Entities;
using Wafish.Storage;
using Wafish.Dashboard.Modules.GatewayConfig.Services;
using Yarp.ReverseProxy.Configuration;

internal static class PluginLifecycleContractChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Audit lifetime: shutdown joins lazy loading and accepted writes before releasing storage", async () =>
        {
            var repository = new AuditRepository();
            await using var audit = new ConfigChangeAuditLog(repository, NullLogger<ConfigChangeAuditLog>.Instance);
            audit.RecordSuccess("test", "committed");
            await repository.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = audit.DisposeAsync().AsTask();
            Check(!shutdown.IsCompleted && ReferenceEquals(shutdown, audit.DisposeAsync().AsTask()), "Shutdown lost audit ownership.");
            try { audit.RecordSuccess("late", "rejected"); throw new Exception("Disposed audit accepted a write."); }
            catch (ObjectDisposedException) { }
            repository.LoadRelease.SetResult();
            await repository.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!shutdown.IsCompleted, "Shutdown abandoned an accepted audit write.");
            repository.WriteRelease.SetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Check(repository.Writes == 1, "Audit write was lost or repeated.");
            var unused = new AuditRepository();
            await new ConfigChangeAuditLog(unused, NullLogger<ConfigChangeAuditLog>.Instance).DisposeAsync();
            Check(!unused.LoadEntered.Task.IsCompleted, "Disposal started a lazy storage read.");
        });
        await run("Plugin lifecycle contract: repeated reconciliation and partial-start cleanup retain ownership", async () =>
        {
            var resource = new Resource { FailStart = true };
            using var coordinator = Coordinator(resource);
            await coordinator.ReconcileAsync();
            Check(resource.Starts == 1 && resource.Handles == 0 && resource.Stops == 1, "Partial startup leaked handles.");
            resource.FailStart = false;
            await coordinator.ReconcileAsync(); await coordinator.ReconcileAsync();
            Check(resource.Starts == 2 && resource.Handles == 1, "Repeated reconciliation duplicated startup.");
            await coordinator.StopAsync(default); await coordinator.StopAsync(default);
            await coordinator.ReconcileAsync();
            Check(resource.Handles == 0 && resource.Starts == 2, "Stopped coordinator restarted a resource.");
        });
        await run("Plugin lifecycle contract: cancelled partial startup performs uncancelled cleanup", async () =>
        {
            var resource = new Resource { BlockStart = true };
            using var coordinator = Coordinator(resource);
            using var cancellation = new CancellationTokenSource();
            var reconcile = coordinator.ReconcileAsync(cancellation.Token);
            await resource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await reconcile; throw new Exception("Startup cancellation did not propagate."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Check(resource.Handles == 0 && resource.Stops == 1 && !resource.StopTokenWasCancelled, "Cancellation abandoned partial startup ownership.");
        });
        await run("Plugin lifecycle contract: shutdown joins startup and prevents restart after cleanup", async () =>
        {
            var resource = new Resource { BlockStart = true };
            using var coordinator = Coordinator(resource);
            await coordinator.StartAsync(default);
            await resource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.ReconcileAsync();
            Check(resource.Starts == 1 && resource.Handles == 0, "Shutdown raced a later startup.");
        });
        await run("Plugin lifecycle contract: stop and health failures do not skip peer cleanup", async () =>
        {
            var good = new Resource(); var failed = new Resource { FailStop = true, FailHealth = false };
            using var coordinator = Coordinator(good, failed);
            await coordinator.ReconcileAsync();
            failed.FailHealth = true;
            await coordinator.StopAsync(default);
            Check(good.Handles == 0 && failed.Handles == 1 && coordinator.GetRuntimeResources("proxy-log").Any(r => r.Health == PluginResourceHealthStatus.Faulted),
                "Failed stop hid its state or skipped a peer.");
            Check(coordinator.GetRuntimeResources("proxy-log").All(r => r.Message?.Contains("fixture-secret") != true), "Health failure exposed exception details.");
            failed.FailStop = false; failed.FailHealth = false;
            await coordinator.StopAsync(default);
            Check(failed.Handles == 0, "Repeated shutdown could not finish failed cleanup.");
        });
    }
    private static PluginResourceLifecycleCoordinator Coordinator(params Resource[] resources)
    {
        var manager = DispatchProxy.Create<IGatewayPluginManager, Manager>();
        var publisher = new GatewaySnapshotPublisher();
        publisher.Publish(GatewaySnapshot.Empty with { Version = 1, Routes = [new RouteConfig { RouteId = "contract", Match = new() { Path = "/contract" } }],
            RoutePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add("contract",
                [new("contract", "proxy-log", PluginBindingScope.Route, "contract", "{\"enabled\":true}", 1, 1, 0)]) });
        return new(manager, new GatewayPluginExecutionPlanProvider(publisher, NullLogger<GatewayPluginExecutionPlanProvider>.Instance), resources,
            NullLogger<PluginResourceLifecycleCoordinator>.Instance);
    }
    private sealed class AuditRepository : IAuditLogRepository
    {
        public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes;
        public async Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsAsync(int limit = 200, CancellationToken ct = default)
        { LoadEntered.TrySetResult(); await LoadRelease.Task.WaitAsync(ct); return []; }
        public Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsByTargetAsync(string target, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntity>>([]);
        public async Task SaveAuditLogAsync(AuditLogEntity audit, CancellationToken ct = default)
        { WriteEntered.TrySetResult(); await WriteRelease.Task.WaitAsync(ct); Interlocked.Increment(ref Writes); }
        public Task DeleteOldAuditLogsAsync(int keepCount, CancellationToken ct = default) => Task.CompletedTask;
    }
    public class Manager : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name == nameof(IGatewayPluginManager.IsPluginEnabled) ? true : null;
    }
    private sealed class Resource : IPluginRuntimeResource
    {
        public string PluginId => "proxy-log";
        public string ResourceId { get; } = "proxy-log:" + Guid.NewGuid().ToString("N");
        public string ResourceType => "contract-fixture";
        public bool FailStart, BlockStart, FailStop, FailHealth, StopTokenWasCancelled;
        public int Starts, Stops, Handles;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask StartResourceAsync(CancellationToken ct)
        {
            if (Handles != 0) return;
            ct.ThrowIfCancellationRequested();
            Starts++; Handles++;
            Entered.TrySetResult();
            if (BlockStart) await Task.Delay(Timeout.Infinite, ct);
            if (FailStart) throw new IOException("Injected partial startup failure.");
        }
        public ValueTask StopResourceAsync(CancellationToken ct)
        {
            StopTokenWasCancelled = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            Stops++;
            if (FailStop) throw new IOException("Injected stop failure.");
            Handles = 0;
            return ValueTask.CompletedTask;
        }
        public ValueTask<PluginRuntimeResourceSnapshot> CheckHealthAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailHealth) throw new IOException("fixture-secret");
            return ValueTask.FromResult(new PluginRuntimeResourceSnapshot(ResourceId, ResourceType, Handles > 0,
                Handles > 0 ? PluginResourceHealthStatus.Healthy : PluginResourceHealthStatus.Stopped, null, null, null, new Dictionary<string, long>()));
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
