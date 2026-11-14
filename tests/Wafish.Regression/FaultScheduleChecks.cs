using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wafish.Models;
using Wafish.Plugin.ProxyLog;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Plugin.ProxyLog.Services;
using Wafish.Storage;
using Wafish.Dashboard.Modules.GatewayConfig.Services;
using Yarp.ReverseProxy.Configuration;
using Fixture = ConfigurationTransactionChecks.Fixture;

internal static class FaultScheduleChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Fault schedules: persistence, publication, and commit cancellation preserve revisions", ConfigurationAsync);
        await run("Fault schedules: retry recovery and cancelled delays preserve attempt ownership", RetryAsync);
        await run("Fault schedules: log drain cancellation accounts for queued and in-flight records", LogDrainAsync);
        await run("Fault schedules: audit loading retains recent writes within the capacity limit", AuditAsync);
    }
    private static async Task AuditAsync()
    {
        foreach (var seed in DeterministicFaultSchedule.Seeds)
        {
            var schedule = new DeterministicFaultSchedule("audit-load", seed);
            await schedule.RunAsync(async () =>
            {
                var repository = new ScheduledAuditRepository(schedule);
                await using var audit = new ConfigChangeAuditLog(repository, NullLogger<ConfigChangeAuditLog>.Instance);
                var recent = 10 + new Random(seed).Next(10);
                try
                {
                    audit.GetRecent();
                    await schedule.WaitAsync("load");
                    for (var index = 0; index < recent; index++) audit.Record(new ConfigChangeAudit { Id = "recent-" + index, Timestamp = DateTime.UnixEpoch.AddDays(2).AddSeconds(index), Success = true });
                    Check(audit.Count == recent, "Pending reads hid accepted audit writes.");
                }
                finally { schedule.Release("load"); }
                await repository.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(audit.Count == 200 && audit.GetPage(1, 200).Total == 200, "Background loading exceeded the audit capacity.");
                Check(audit.GetRecent(200).Count(entry => entry.Id.StartsWith("recent-")) == recent, "Background loading evicted newer entries.");
                audit.Record(new ConfigChangeAudit { Id = "last", Timestamp = DateTime.UnixEpoch.AddDays(3) });
                Check(audit.GetRecent(200).Count(entry => entry.Id.StartsWith("recent-")) == recent && audit.GetRecent(1).Single().Id == "last", "The next write evicted a recent entry instead of the oldest entry.");
                Check(audit.EvictedCount == recent + 1, "Audit eviction accounting differs.");
                schedule.Record("bounded-recent-entries-retained");
            });
        }
    }
    private sealed class ScheduledAuditRepository(DeterministicFaultSchedule schedule) : IAuditLogRepository
    {
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsAsync(int limit = 200, CancellationToken cancellationToken = default)
        {
            await schedule.HoldAsync("load", cancellationToken);
            return Enumerable.Range(0, 200).Reverse().Select(index => new AuditLogEntity { Id = "stored-" + index, Timestamp = DateTime.UnixEpoch.AddSeconds(index) }).ToArray();
        }
        public Task SaveAuditLogAsync(AuditLogEntity audit, CancellationToken cancellationToken = default) { Saved.TrySetResult(); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsByTargetAsync(string target, int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditLogEntity>>([]);
        public Task DeleteOldAuditLogsAsync(int keepCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private static async Task ConfigurationAsync()
    {
        foreach (var seed in DeterministicFaultSchedule.Seeds)
        {
            var schedule = new DeterministicFaultSchedule("configuration", seed);
            await schedule.RunAsync(async () =>
            {
                foreach (var operation in schedule.Order("storage-reject", "publication-reject", "cancel-owned-commit"))
                {
                    await using var fixture = await Fixture.Create();
                    using var cancellation = new CancellationTokenSource();
                    schedule.Record(operation);
                    var gate = operation + ":save";
                    fixture.Store.BeforeSave = async () =>
                    {
                        await schedule.HoldAsync(gate);
                        if (operation == "storage-reject") throw new IOException("Scheduled storage rejection.");
                    };
                    if (operation == "publication-reject") fixture.Publisher.Fail = true;
                    var pending = fixture.Config.TryAddClusterConfig(new ClusterConfig { ClusterId = "scheduled", Destinations = new Dictionary<string, DestinationConfig>
                        { ["one"] = new() { Address = "http://localhost:9000/" } } }, "schedule", "fixture", cancellation.Token);
                    try
                    {
                        await schedule.WaitAsync(gate);
                        Check(fixture.Config.GetClusters().Count == 0, "A blocked commit published its candidate.");
                        if (operation == "cancel-owned-commit") { schedule.Record("cancel-caller"); cancellation.Cancel(); }
                    }
                    finally { schedule.Release(gate); }
                    var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    schedule.Record("outcome:" + result.Status);
                    var expected = operation switch { "storage-reject" => ConfigurationUpdateStatus.StorageFailed,
                        "publication-reject" => ConfigurationUpdateStatus.ActivationFailed, _ => ConfigurationUpdateStatus.Applied };
                    Check(result.Status == expected, "Scheduled failure changed classification.");
                    Check((await fixture.Store.LoadAsync()).Version == result.PersistedVersion && fixture.Config.GetDynamicConfig()!.Version == result.ActiveVersion,
                        "Scheduled outcome lost a confirmed revision.");
                    Check(operation != "publication-reject" || result.PersistedVersion > result.ActiveVersion, "Publication failure hid a stored revision.");
                }
            });
        }
    }
    private static async Task RetryAsync()
    {
        foreach (var seed in DeterministicFaultSchedule.Seeds)
        {
            var schedule = new DeterministicFaultSchedule("retry", seed);
            await schedule.RunAsync(async () =>
            {
                foreach (var operation in schedule.Order("recover", "cancel-delay"))
                {
                    schedule.Record(operation);
                    var clock = new ScheduledClock(TimeSpan.FromMilliseconds(100));
                    using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).BuildServiceProvider();
                    var fixture = new RetryStreamingChecks.Fixture(policy: new() { MaxRetries = 1, BackoffBaseMs = 100,
                        BackoffJitterMs = 0, TimeoutSeconds = 5, TotalTimeoutSeconds = 30 });
                    var context = fixture.Context(); context.RequestServices = services;
                    using var cancellation = new CancellationTokenSource(); context.RequestAborted = cancellation.Token;
                    fixture.Respond = response =>
                    {
                        schedule.Record("attempt:" + fixture.Calls);
                        response.Response.StatusCode = fixture.Calls == 1 ? 503 : 200;
                        return Task.CompletedTask;
                    };
                    var pending = fixture.Pipeline(context);
                    await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Check(fixture.Calls == 1 && !pending.IsCompleted, "Retry delay did not retain its attempt.");
                    if (operation == "cancel-delay")
                    {
                        schedule.Record("cancel-caller"); cancellation.Cancel();
                        try { await pending; throw new Exception("Retry cancellation was swallowed."); }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                        Check(fixture.Calls == 1, "Cancelled delay created a second attempt.");
                    }
                    else
                    {
                        schedule.Record("advance:99ms"); clock.Advance(TimeSpan.FromMilliseconds(99));
                        Check(fixture.Calls == 1, "Retry started before its scheduled boundary.");
                        schedule.Record("advance:1ms"); clock.Advance(TimeSpan.FromMilliseconds(1));
                        await pending.WaitAsync(TimeSpan.FromSeconds(10));
                        Check(fixture.Calls == 2 && context.Response.StatusCode == 200, "Scheduled retry failed to recover.");
                    }
                    await context.Response.Body.DisposeAsync();
                }
            });
        }
    }
    private static async Task LogDrainAsync()
    {
        foreach (var seed in DeterministicFaultSchedule.Seeds)
        {
            var schedule = new DeterministicFaultSchedule("log-drain", seed);
            await schedule.RunAsync(async () =>
            {
                await using var fixture = await Fixture.Create();
                var repository = DispatchProxy.Create<IProxyLogRepository, ReviewAccountingChecks.LogRepositoryProxy>();
                var proxy = (ReviewAccountingChecks.LogRepositoryProxy)repository;
                proxy.Inner = fixture.App.Services.GetRequiredService<IProxyLogRepository>();
                proxy.BeforeWrite = ct => schedule.HoldAsync("write", ct);
                var count = 100 + new Random(seed).Next(1, 5) * 100;
                schedule.Record("submit:" + count);
                var store = new ProxyLogStore(channelCapacity: count);
                for (var i = 0; i < count; i++) store.Add(new() { Timestamp = DateTime.UtcNow, EventType = LogEventType.ProxyResponse, StatusCode = 200 });
                var resource = new AsyncLogPersistenceService(store, new SqliteProxyLogWriter(repository, NullLogger<SqliteProxyLogWriter>.Instance),
                    new ProxyLogRuntimeSettings(Options.Create(new ProxyLogPluginOptions())), null!, NullLogger<AsyncLogPersistenceService>.Instance);
                await resource.StartResourceAsync(default);
                try
                {
                    await schedule.WaitAsync("write");
                    schedule.Record("cancel-drain");
                    try { await resource.StopResourceAsync(new CancellationToken(true)); }
                    catch (OperationCanceledException) { }
                    await resource.StopResourceAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                    Check(resource.WrittenCount == 0 && resource.DroppedCount == count && store.PendingCount == 0,
                        "Cancelled drain lost ownership of submitted records.");
                    Check((await proxy.Inner.SearchAsync(1, 1)).TotalCount == 0, "Cancelled write persisted records after shutdown.");
                    schedule.Record("discarded:" + resource.DroppedCount);
                }
                finally { schedule.Release("write"); await resource.StopResourceAsync(default); }
            });
        }
    }
    private sealed class ScheduledClock(TimeSpan watched) : TimeProvider
    {
        private readonly ReviewReleaseChecks.ManualTime _inner = new();
        public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public void Advance(TimeSpan elapsed) => _inner.Advance(elapsed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == watched) Scheduled.TrySetResult();
            return timer;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
