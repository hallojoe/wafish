namespace Wafish.Storage;

/// <summary>Optional atomic configuration receipts. Keys remain reserved for at least 24 hours after commit.</summary>
public interface IIdempotentConfigurationSnapshotRepository : IConfigurationSnapshotRepository
{
    Task<ConfigurationMutationReceipt?> GetReceiptAsync(string key, CancellationToken cancellationToken = default);
    Task SaveWithReceiptAsync(ConfigurationSnapshot snapshot, long expectedVersion, string key, string fingerprint, CancellationToken cancellationToken = default);
    Task CompleteReceiptAsync(string key, string resultJson, CancellationToken cancellationToken = default);
}

/// <summary>A committed request fingerprint and its original result. Null ResultJson means activation acknowledgement is unresolved.</summary>
public sealed record ConfigurationMutationReceipt(string Key, string Fingerprint, long Version, string? ResultJson);

public sealed class ConfigurationReceiptExistsException(string key) : Exception("A configuration receipt already exists for this key.")
{
    public string Key { get; } = key;
}
