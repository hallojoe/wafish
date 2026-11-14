using System.Collections.Concurrent;
using System.Text.Json;
using Wafish.Models;
using Wafish.Services;
using Wafish.Storage;
using Microsoft.Extensions.Logging;

namespace Wafish.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Audit log store for gateway configuration changes using <see cref="IAuditLogRepository"/>.
/// Uses in-memory <see cref="ConcurrentQueue{T}"/> for fast writes,
/// and persists to repository for structured query capability.
/// Thread-safe, bounded by max capacity (ring-buffer style).
///
/// Config-change events are queued for the administration event dispatcher
/// to guarantee delivery even when the first request arrives before all consumers are constructed.
/// </summary>
public class ConfigChangeAuditLog : IConfigChangeAuditLog, IAsyncDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const int MaxCapacity = 200;

    private readonly ConcurrentQueue<ConfigChangeAudit> _entries = new();
    private int _totalCount;
    private long _evictedCount;
    private readonly Lazy<Task> _loadTask;
    private readonly object _lifetimeGate = new();
    private readonly HashSet<Task> _pendingWrites = [];
    private Task? _shutdownTask;

    private readonly ConcurrentQueue<PendingNotification> _pendingNotifications = new();
    private readonly IAuditLogRepository _auditRepo;
    private readonly ILogger<ConfigChangeAuditLog> _logger;

    /// <inheritdoc />
    public event Action<string, string, string?, object?>? OnConfigChanged;

    public ConfigChangeAuditLog(IAuditLogRepository auditRepo, ILogger<ConfigChangeAuditLog> logger)
    {
        _auditRepo = auditRepo;
        _logger = logger;
        _loadTask = new Lazy<Task>(() => Task.Run(LoadPersistedAsync));
    }

    /// <summary>Load persisted entries from repository on first access.</summary>
    private async Task LoadPersistedAsync()
    {
        try
        {
            var persisted = await _auditRepo.GetAuditLogsAsync(MaxCapacity);
            lock (_lifetimeGate)
            {
                // Merge before trimming: writes accepted during loading must remain newer than stored history.
                var merged = persisted.Select(entity => entity.ToConfigChangeAudit()).Concat(_entries)
                    .OrderBy(entry => entry.Timestamp).ToArray();
                var skipped = Math.Max(0, merged.Length - MaxCapacity);
                _entries.Clear();
                foreach (var entry in merged.Skip(skipped)) _entries.Enqueue(entry);
                _totalCount = merged.Length - skipped;
                Interlocked.Add(ref _evictedCount, skipped);
            }
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                _logger.LogDebug("Loaded {Count} audit entries from repository", persisted.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted audit entries");
        }
    }

    /// <summary>Non-blocking ensure loaded for synchronous contexts.</summary>
    private void EnsureLoaded()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_shutdownTask != null, this);
            _ = _loadTask.Value;
        }
    }

    /// <inheritdoc />
    public void Record(ConfigChangeAudit entry)
    {
        lock (_lifetimeGate)
        {
            EnsureLoaded();

            _entries.Enqueue(entry);
            var count = Interlocked.Increment(ref _totalCount);

            while (count > MaxCapacity)
            {
                if (_entries.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _totalCount);
                    Interlocked.Increment(ref _evictedCount);
                    count--;
                }
                else
                {
                    break;
                }
            }

            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                _logger.LogDebug("Audit queued: {Action} on '{Target}' — {Status}",
                    entry.Action, entry.Target,
                    entry.Success ? "OK" : $"FAIL: {entry.ErrorMessage}");
            }

            // Retain background ownership until host disposal joins every accepted write.
            Task? write = null;
            write = Task.Run(async () =>
            {
                try
                {
                    // Finish the initial read before a new write can appear in that read.
                    await _loadTask.Value.ConfigureAwait(false);
                    var entity = entry.ToEntity();
                    entity.BeforeData = SafeErrorMessages.Redact(entity.BeforeData);
                    entity.AfterData = SafeErrorMessages.Redact(entity.AfterData);
                    entity.ErrorMessage = SafeErrorMessages.Redact(entity.ErrorMessage);
                    await _auditRepo.SaveAuditLogAsync(entity);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist audit entry"); }
                finally { lock (_lifetimeGate) _pendingWrites.Remove(write!); }
            });
            _pendingWrites.Add(write);

            // Queue the notification for the background dispatcher to deliver.
            // This is reliable regardless of when consumers are constructed.
            if (entry.Success)
            {
                _pendingNotifications.Enqueue(new PendingNotification
                {
                    EventType = entry.Action,
                    Target = entry.Target,
                    Operator = entry.Operator,
                    Details = entry.After
                });
            }
        }
    }

    /// <inheritdoc />
    public void RecordSuccess(string action, string target, string? operatorName = null, string? clientIp = null,
        object? before = null, object? after = null)
    {
        Record(new ConfigChangeAudit
        {
            Action = action,
            Target = target,
            Operator = operatorName,
            ClientIp = clientIp,
            Before = before != null ? JsonSerializer.Serialize(before, _jsonOptions) : null,
            After = after != null ? JsonSerializer.Serialize(after, _jsonOptions) : null,
            Success = true
        });
    }

    /// <inheritdoc />
    public void RecordFailure(string action, string target, string errorMessage, string? operatorName = null)
    {
        Record(new ConfigChangeAudit
        {
            Action = action,
            Target = target,
            Operator = operatorName,
            Success = false,
            ErrorMessage = errorMessage
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<ConfigChangeAudit> GetRecent(int count = 50)
    {
        EnsureLoaded();

        ConfigChangeAudit[] entries;
        lock (_lifetimeGate) entries = _entries.ToArray();
        if (entries.Length <= 1)
        {
            return entries.Take(count).ToList();
        }

        Array.Sort(entries, (a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return entries.Take(count).ToList();
    }

    /// <inheritdoc />
    public (IReadOnlyList<ConfigChangeAudit> Entries, int Total) GetPage(int page = 1, int pageSize = 50, string? action = null)
    {
        EnsureLoaded();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxCapacity);

        ConfigChangeAudit[] snapshot;
        lock (_lifetimeGate) snapshot = _entries.ToArray();
        IEnumerable<ConfigChangeAudit> query = snapshot.OrderByDescending(e => e.Timestamp);

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(e => e.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        }

        var list = query.ToList();
        var total = list.Count;
        var entries = list
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return (entries, total);
    }

    /// <inheritdoc />
    public int Count { get { lock (_lifetimeGate) return _entries.Count; } }

    /// <inheritdoc />
    public long EvictedCount => Volatile.Read(ref _evictedCount);

    /// <summary>Query audit logs by target from repository.</summary>
    public async Task<IReadOnlyList<ConfigChangeAudit>> GetByTargetAsync(string target, int count = 50)
    {
        var entities = await _auditRepo.GetAuditLogsByTargetAsync(target, count);
        return entities.ToConfigChangeAudits();
    }

    /// <inheritdoc />
    public bool TryDequeuePendingNotification(out PendingNotification notification)
        => _pendingNotifications.TryDequeue(out notification);

    /// <inheritdoc />
    public void InvokeOnConfigChanged(string eventType, string target, string? @operator, object? details)
        => OnConfigChanged?.Invoke(eventType, target, @operator, details);
    /// <summary>Stop admission and join the initial read and all accepted background writes.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_shutdownTask == null)
            {
                var work = _pendingWrites.ToList();
                if (_loadTask.IsValueCreated) work.Add(_loadTask.Value);
                _shutdownTask = Task.WhenAll(work);
            }
            return new ValueTask(_shutdownTask);
        }
    }

}
