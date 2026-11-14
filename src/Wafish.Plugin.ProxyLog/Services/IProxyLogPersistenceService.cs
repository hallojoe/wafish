namespace Wafish.Plugin.ProxyLog.Services;

/// <summary>
/// Interface for the log persistence coordination service.
/// Implemented by AsyncLogPersistenceService (C2) which reads from ProxyLogStore's
/// persistence Channel and writes batches to SQLite via SqliteProxyLogWriter.
/// </summary>
public interface IProxyLogPersistenceService
{
    /// <summary>
    /// Number of log entries discarded by admission, exhausted writes, or shutdown.
    /// These entries exist in the in-memory ring buffer but were not persisted to SQLite.
    /// </summary>
    long DroppedCount { get; }

    /// <summary>
    /// Total number of log entries successfully written to SQLite since startup.
    /// </summary>
    long WrittenCount { get; }
}
