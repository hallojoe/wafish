using Wafish.Storage.Entities;

namespace Wafish.Storage;

/// <summary>Optional atomic quota support. Charges UTF-8 fields plus fixed row allowances, excluding database indexes and WAL.</summary>
public interface IQuotaProxyLogRepository
{
    Task<ProxyLogQuotaResult> WriteBatchWithQuotaAsync(IReadOnlyList<ProxyLogMetaEntity> metadata,
        IReadOnlyList<ProxyLogBodyEntity?> bodies, ProxyLogQuota quota, CancellationToken cancellationToken = default);
}

/// <summary>Retained logical byte limits. Zero disables the corresponding limit. Negative limits are invalid.</summary>
public sealed record ProxyLogQuota(long MetadataBytes, long BodyBytes);

/// <summary>Committed quota cleanup result. Discards include bodies removed with their metadata.</summary>
public sealed record ProxyLogQuotaResult(long MetadataBytes, long BodyBytes, long DiscardedMetadata, long DiscardedBodies);
