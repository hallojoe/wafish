namespace Wafish.Storage;

/// <summary>
/// Config history repository for configuration version management.
/// </summary>
public interface IConfigHistoryRepository
{
    Task<ConfigHistoryEntity?> GetConfigHistoryAsync(string versionId, CancellationToken ct = default);
    /// <summary>Return 1–500 rows, ordered by creation time descending, then version ID descending.</summary>
    Task<IReadOnlyList<ConfigHistoryEntity>> GetConfigHistoryListAsync(int limit = 50, CancellationToken ct = default);
    Task SaveConfigHistoryAsync(ConfigHistoryEntity history, CancellationToken ct = default);
    Task DeleteConfigHistoryAsync(string versionId, CancellationToken ct = default);
    Task DeleteOldConfigHistoryAsync(int keepCount, CancellationToken ct = default);
    Task ClearConfigHistoryAsync(CancellationToken ct = default);
}
