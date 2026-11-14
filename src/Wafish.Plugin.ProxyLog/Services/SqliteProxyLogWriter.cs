using Wafish.Plugin.ProxyLog.Models;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Wafish.Plugin.ProxyLog.Services;

/// <summary>
/// High-level batch writer that converts LogEntry objects into ProxyLogMetaEntity + ProxyLogBodyEntity
/// and delegates the actual SQLite write to IProxyLogRepository.WriteBatchAsync().
/// 
/// This service sits between the AsyncLogPersistenceService (Channel consumer) and
/// the IProxyLogRepository (SQLite data access layer), handling the LogEntry → Entity mapping.
/// </summary>
public sealed class SqliteProxyLogWriter
{
    private readonly IProxyLogRepository _repository;
    private readonly ILogger<SqliteProxyLogWriter> _logger;
    private long _writtenCount;
    private readonly ProxyLogQuota _quota;
    private ProxyLogQuotaResult _quotaUsage = new(0, 0, 0, 0);
    private long _discardedBodies, _discardedMetadata;
    public ProxyLogQuotaResult QuotaUsage => Volatile.Read(ref _quotaUsage);
    public long QuotaDiscardedBodies => Interlocked.Read(ref _discardedBodies);
    public long QuotaDiscardedMetadata => Interlocked.Read(ref _discardedMetadata);

    public SqliteProxyLogWriter(IProxyLogRepository repository, ILogger<SqliteProxyLogWriter> logger)
    {
        _quota = new(0, 0);
        _repository = repository;
        _logger = logger;
    }

    public SqliteProxyLogWriter(IProxyLogRepository repository, ILogger<SqliteProxyLogWriter> logger,
        Microsoft.Extensions.Options.IOptions<ProxyLogPluginOptions> options) : this(repository, logger)
    {
        _quota = new(options.Value.LogMetadataQuotaBytes, options.Value.LogBodyQuotaBytes);
        if (_quota.MetadataBytes < 0 || _quota.BodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(options));
        if ((_quota.MetadataBytes > 0 || _quota.BodyBytes > 0) && repository is not IQuotaProxyLogRepository)
            throw new InvalidOperationException("Configured log quotas require a quota-capable repository.");
    }

    /// <summary>Total number of log entries successfully written to SQLite.</summary>
    public long WrittenCount => Volatile.Read(ref _writtenCount);

    /// <summary>
    /// Convert a batch of LogEntry objects to entity pairs and write to SQLite.
    /// Each LogEntry is split into a lightweight meta row and a body row (if it has large fields).
    /// </summary>
    public async Task WriteBatchAsync(IEnumerable<LogEntry> entries, CancellationToken ct = default)
    {
        var capacity = entries.TryGetNonEnumeratedCount(out var count) ? count : 0;
        var metaList = new List<ProxyLogMetaEntity>(capacity);
        var bodyList = new List<ProxyLogBodyEntity?>(capacity);

        foreach (var entry in entries)
        {
            metaList.Add(MapToMeta(entry));
            bodyList.Add(MapToBody(entry));
        }

        if (metaList.Count == 0)
        {
            return;
        }

        try
        {
            if (_quota.MetadataBytes > 0 || _quota.BodyBytes > 0)
            {
                var result = await ((IQuotaProxyLogRepository)_repository).WriteBatchWithQuotaAsync(metaList, bodyList, _quota, ct);
                Volatile.Write(ref _quotaUsage, result);
                Interlocked.Add(ref _discardedBodies, result.DiscardedBodies);
                Interlocked.Add(ref _discardedMetadata, result.DiscardedMetadata);
            }
            else await _repository.WriteBatchAsync(metaList, bodyList, ct);
            Interlocked.Add(ref _writtenCount, metaList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write batch of {Count} log entries to SQLite", metaList.Count);
            throw;
        }
    }

    /// <summary>
    /// Clean up expired log records and run WAL checkpoint.
    /// </summary>
    public async Task CleanupAsync(int metaRetentionDays, int bodyRetentionDays, CancellationToken ct = default)
    {
        try
        {
            await _repository.CleanupAsync(metaRetentionDays, bodyRetentionDays, ct);
            await _repository.CheckpointAsync(ct);
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("Log cleanup completed: meta={MetaDays}d, body={BodyDays}d", metaRetentionDays, bodyRetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log cleanup failed (non-critical, will retry on next cycle)");
        }
    }

    private static ProxyLogMetaEntity MapToMeta(LogEntry entry) => new()
    {
        Timestamp = entry.Timestamp.ToUniversalTime().ToString("O"),
        EventType = entry.EventType.ToString(),
        Level = entry.Level,
        RouteId = entry.RouteId,
        ClusterId = entry.ClusterId,
        Method = entry.Method,
        UpstreamPath = entry.UpstreamPath,
        StatusCode = entry.StatusCode ?? 0,
        ElapsedMs = entry.ElapsedMs ?? 0,
        TraceId = entry.TraceId,
        HasRequestBody = entry.RequestBody != null ? 1 : 0,
        HasResponseBody = entry.ResponseBody != null ? 1 : 0,
        DownstreamUrl = entry.DownstreamUrl
    };

    private static ProxyLogBodyEntity? MapToBody(LogEntry entry)
    {
        // Only create a body row if there are large fields to store
        if (entry.RequestBody == null &&
            entry.ResponseBody == null &&
            entry.RequestHeaders == null &&
            entry.ResponseHeaders == null &&
            entry.DownstreamBody == null &&
            entry.Exception == null &&
            string.IsNullOrEmpty(entry.Message))
        {
            return null;
        }

        return new ProxyLogBodyEntity
        {
            MetaId = 0, // Will be set by repository after meta insert
            Message = entry.Message,
            RequestBody = entry.RequestBody,
            ResponseBody = entry.ResponseBody,
            RequestHeaders = SerializeHeaders(entry.RequestHeaders),
            ResponseHeaders = SerializeHeaders(entry.ResponseHeaders),
            DownstreamBody = entry.DownstreamBody,
            Exception = entry.Exception
        };
    }

    private static string? SerializeHeaders(HeaderList? headers)
    {
        if (headers == null || headers.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(headers);
    }
}
