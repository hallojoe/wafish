namespace Wafish.Storage;

/// <summary>
/// Audit log repository for config change audit persistence.
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>Return 1–500 rows, ordered by timestamp descending, then audit ID descending.</summary>
    Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsAsync(int limit = 200, CancellationToken ct = default);
    /// <summary>Use the same bounds and stable ordering as GetAuditLogsAsync for one target.</summary>
    Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsByTargetAsync(string target, int limit = 50, CancellationToken ct = default);
    Task SaveAuditLogAsync(AuditLogEntity audit, CancellationToken ct = default);
    Task DeleteOldAuditLogsAsync(int keepCount, CancellationToken ct = default);
}
