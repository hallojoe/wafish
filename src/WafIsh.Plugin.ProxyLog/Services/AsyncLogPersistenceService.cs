using WafIsh.Infrastructure.Middleware;
using WafIsh.Plugins;
using WafIsh.Plugin.ProxyLog.Models;
using WafIsh.Storage;
using WafIsh.Storage.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace WafIsh.Plugin.ProxyLog.Services;

/// <summary>
/// Background service that consumes log entries from ProxyLogStore's persistence Channel
/// and writes batches to SQLite via SqliteProxyLogWriter.
/// </summary>
/// <remarks>
/// Flow: ProxyLogStore.Add() → bounded Channel → AsyncLogPersistenceService → SqliteProxyLogWriter → SQLite
/// 
/// Features:
/// - Batch writes: accumulates up to 100 entries or flushes every 500ms
/// - Hourly cleanup: deletes expired meta/body rows + WAL checkpoint
/// - DroppedCount: tracks admission, retry-exhaustion, and shutdown discards
/// - WrittenCount: total entries successfully persisted
/// - Implements IProxyLogPersistenceService for DI access to stats
/// </remarks>
public sealed class AsyncLogPersistenceService : IProxyLogPersistenceService, IPluginRuntimeResource
{
    private readonly ProxyLogStore _logStore;
    private readonly SqliteProxyLogWriter _writer;
    private readonly ProxyLogRuntimeSettings _runtimeSettings;
    private readonly IGatewayPluginManager _pluginManager;
    private readonly ILogger<AsyncLogPersistenceService> _logger;
    private CancellationTokenSource? _drainCancellation;
    private long _lastCleanup;
    private CancellationTokenSource? _cts;
    private Task? _consumeTask;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _stoppedAt;
    private Exception? _lastError;
    private long _failureCount;

    public string PluginId => "proxy-log";
    public string ResourceId => "proxy-log:channel-sqlite-writer";
    public string ResourceType => "channel-writer";

    private readonly TimeProvider _time;
    public AsyncLogPersistenceService(ProxyLogStore logStore, SqliteProxyLogWriter writer, ProxyLogRuntimeSettings runtimeSettings, IGatewayPluginManager pluginManager, ILogger<AsyncLogPersistenceService> logger)
        : this(logStore, writer, runtimeSettings, pluginManager, logger, TimeProvider.System) { }

    public AsyncLogPersistenceService(
        ProxyLogStore logStore,
        SqliteProxyLogWriter writer,
        ProxyLogRuntimeSettings runtimeSettings,
        IGatewayPluginManager pluginManager,
        ILogger<AsyncLogPersistenceService> logger, TimeProvider timeProvider)
    {
        _time = timeProvider;
        _lastCleanup = _time.GetTimestamp();
        _logStore = logStore;
        _writer = writer;
        _runtimeSettings = runtimeSettings;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public long DroppedCount => _logStore.DroppedCount;

    /// <inheritdoc />
    public long WrittenCount => _logStore.PersistedCount;

    public ValueTask StartResourceAsync(CancellationToken cancellationToken)
    {
        if (_consumeTask is { IsCompleted: false }) return ValueTask.CompletedTask;
        var settings = _runtimeSettings.Current;
        if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
        {
            _logger.LogInformation("AsyncLogPersistenceService starting: enabled={Enabled}, meta={MetaDays}d, body={BodyDays}d",
                settings.PersistenceEnabled, settings.MetaRetentionDays, settings.BodyRetentionDays);
        }
        _lastError = null;
        _startedAt = _time.GetUtcNow();
        _cts = new CancellationTokenSource();
        _drainCancellation = new CancellationTokenSource();
        _consumeTask = Task.Run(() => ConsumeLoopAsync(_cts.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopResourceAsync(CancellationToken cancellationToken)
    {
        if (_consumeTask is null) return;
        var drain = _drainCancellation!;
        using var drainTimer = _time.CreateTimer(_ => drain.Cancel(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        using var stopRegistration = cancellationToken.Register(() => drain.Cancel());
        if (_cts is not null) await _cts.CancelAsync();
        try { await _consumeTask.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _lastError = ex;
            Interlocked.Increment(ref _failureCount);
            _logger.LogWarning(ex, "AsyncLogPersistenceService consume task stopped with error");
        }
        stopRegistration.Dispose();
        await drainTimer.DisposeAsync();
        drain.Dispose();
        _drainCancellation = null;
        _cts?.Dispose();
        _cts = null;
        _consumeTask = null;
        _stoppedAt = _time.GetUtcNow();
        if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
        {
            _logger.LogInformation("AsyncLogPersistenceService stopped. Written: {WrittenCount}, Dropped: {DroppedCount}", WrittenCount, DroppedCount);
        }
    }

    public ValueTask<PluginRuntimeResourceSnapshot> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var running = _consumeTask is { IsCompleted: false };
        var health = _lastError is not null ? PluginResourceHealthStatus.Faulted :
            running ? PluginResourceHealthStatus.Healthy : PluginResourceHealthStatus.Stopped;
        return ValueTask.FromResult(new PluginRuntimeResourceSnapshot(ResourceId, ResourceType, running, health,
            _startedAt, _stoppedAt, _lastError?.Message, new Dictionary<string, long>
            {
                ["submittedEntries"] = _logStore.SubmittedCount,
                ["pendingEntries"] = _logStore.PendingCount,
                ["writtenEntries"] = WrittenCount,
                ["droppedEntries"] = DroppedCount,
                ["failures"] = Interlocked.Read(ref _failureCount),
                ["memoryBytes"] = _logStore.GetEstimatedMemoryBytes()
            }));
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        const int batchSize = 100;
        var reader = _logStore.PersistenceReader;
        var batch = new List<LogEntry>(batchSize);

        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                if (!reader.TryRead(out var first))
                    continue;

                batch.Add(first);
                using var flushDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500), _time);
                using var flushDelay = CancellationTokenSource.CreateLinkedTokenSource(ct, flushDeadline.Token);
                while (batch.Count < batchSize)
                {
                    while (batch.Count < batchSize && reader.TryRead(out var entry))
                        batch.Add(entry);

                    if (batch.Count >= batchSize || flushDelay.IsCancellationRequested)
                        break;

                    try
                    {
                        if (!await reader.WaitToReadAsync(flushDelay.Token))
                            break;
                    }
                    catch (OperationCanceledException) when (flushDelay.IsCancellationRequested)
                    {
                        break;
                    }
                }

                await FlushBatchAsync(batch, ct);
                batch.Clear();

                if (_time.GetElapsedTime(_lastCleanup) > TimeSpan.FromHours(1))
                {
                    var settings = _runtimeSettings.Current;
                    await _writer.CleanupAsync(settings.MetaRetentionDays, settings.BodyRetentionDays, ct);
                    _lastCleanup = _time.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown transfers the unfinished batch to the bounded final drain.
        }
        finally
        {
            // Preserve the dequeued batch. Drain only the backlog present at shutdown.
            var remaining = reader.Count;
            try
            {
                while (remaining > 0 || batch.Count > 0)
                {
                    while (batch.Count < batchSize && remaining > 0 && reader.TryRead(out var entry))
                    {
                        batch.Add(entry);
                        remaining--;
                    }
                    await FlushBatchAsync(batch, _drainCancellation!.Token);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (_drainCancellation!.IsCancellationRequested)
            {
                _logStore.RecordDiscarded(batch.Count);
                while (remaining-- > 0 && reader.TryRead(out _)) _logStore.RecordDiscarded(1);
            }
        }
    }

    private async Task FlushBatchAsync(List<LogEntry> batch, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _writer.WriteBatchAsync(batch, ct);
                _logStore.RecordPersisted(batch.Count);
                _lastError = null;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _lastError = ex;
                Interlocked.Increment(ref _failureCount);
                _logger.LogWarning(ex,
                    "Failed to write {Count} entries to SQLite (attempt {Attempt}/3)",
                    batch.Count, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), _time, ct);
            }
            catch (Exception ex)
            {
                _lastError = ex;
                Interlocked.Increment(ref _failureCount);
                _logStore.RecordDiscarded(batch.Count);
                _logger.LogError(ex,
                    "Failed to write {Count} entries to SQLite after 3 attempts; batch dropped",
                    batch.Count);
            }
        }
    }
}
