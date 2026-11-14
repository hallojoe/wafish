using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Wafish.Storage;

/// <summary>Optional storage contracts available to a host.</summary>
[Flags, JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageFeature { None = 0, Encryption = 1, ConsistentBackup = 2, AtomicConfiguration = 4, AtomicBindings = 8, ConfigurationReceipts = 16, AtomicChangeSets = 32 }

/// <summary>Provider support without connection strings or secret values.</summary>
public sealed record StorageCapabilities(string Provider, StorageFeature Features, bool EncryptionEnabled);

public interface IStorageCapabilitiesProvider
{
    ValueTask<StorageCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}

public sealed class StorageRequirements
{
    public StorageFeature Required { get; set; }
    public bool RequireEncryptionEnabled { get; set; }
}

public static class StorageRequirementsExtensions
{
    /// <summary>Rejects unsupported requirements during host startup, before requests are accepted.</summary>
    public static IServiceCollection AddWafishStorageRequirements(this IServiceCollection services, Action<StorageRequirements> configure)
    {
        services.Configure(configure);
        services.AddHostedService<StorageRequirementsValidator>();
        return services;
    }
}

internal sealed class StorageRequirementsValidator(IServiceProvider services, IOptions<StorageRequirements> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var requirement = options.Value;
        if (requirement.Required == StorageFeature.None && !requirement.RequireEncryptionEnabled) return;
        var provider = services.GetService<IStorageCapabilitiesProvider>()
            ?? throw new InvalidOperationException("The storage provider does not report capabilities.");
        var capabilities = await provider.GetCapabilitiesAsync(cancellationToken);
        if ((capabilities.Features & requirement.Required) != requirement.Required || requirement.RequireEncryptionEnabled && !capabilities.EncryptionEnabled)
            throw new InvalidOperationException("The storage provider does not satisfy the configured requirements.");
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IStorageBackupProvider
{
    /// <summary>Creates a consistent backup. The caller owns and must dispose the returned backup.</summary>
    Task<StorageBackup> CreateBackupAsync(CancellationToken cancellationToken = default);
}

/// <summary>An owned backup stream. Disposal releases the stream and provider temporary resources.</summary>
public sealed class StorageBackup(Stream content, string fileName, bool encrypted, Func<ValueTask> cleanup) : IAsyncDisposable
{
    private int _disposed;
    public Stream Content { get; } = content;
    public string FileName { get; } = fileName;
    public bool Encrypted { get; } = encrypted;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Content.DisposeAsync(); }
        finally { await cleanup(); }
    }
}
