using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Dashboard.Infrastructure.Notifications;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugins;
using Wafish.Services;
using Wafish.Services.ProxyConfigHealth;
using Wafish.Storage;

namespace Wafish.Dashboard.Infrastructure.Diagnostics;

/// <summary>Collects bounded, allow-listed diagnostics. One collection can own probes at a time.</summary>
public sealed class DiagnosticBundleService(IServiceProvider services)
{
    public const int MaximumBytes = 262144;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<byte[]> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = Guid.NewGuid().ToString("N");
        var sections = new Dictionary<string, object?>
        {
            ["correlationId"] = correlationId,
            ["build"] = typeof(DiagnosticBundleService).Assembly.GetName().Version?.ToString(),
            ["maximumBytes"] = MaximumBytes
        };
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            sections["failures"] = new[] { "collection-busy" };
            return JsonSerializer.SerializeToUtf8Bytes(sections);
        }
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(2));
        var token = lifetime.Token;
        var probes = new Dictionary<string, Task<object?>>
        {
            ["revisions"] = Probe(async () => await services.GetRequiredService<DynamicYarpConfigService>().GetRevisionsAsync(token)),
            ["storage"] = Probe(async () =>
            {
                var provider = services.GetService<IStorageCapabilitiesProvider>();
                if (provider is null) return new { supported = false };
                var info = await provider.GetCapabilitiesAsync(token);
                return new { supported = true, provider = info.Provider == "sqlite" ? "sqlite" : "custom", features = (int)info.Features, info.EncryptionEnabled };
            }),
            ["plugins"] = Probe(async () =>
            {
                var manager = services.GetRequiredService<IPluginRuntimeDomainManager>();
                var domain = manager.Current;
                if (!domain.TryAcquire(out var lease)) throw new InvalidOperationException();
                using (lease)
                {
                    var plugins = new List<object>();
                    foreach (var plugin in domain.Plugins.Values.OrderBy(p => p.Manifest.Order).Take(100))
                    {
                        token.ThrowIfCancellationRequested();
                        var status = plugin is IPluginHealthProbe health
                            ? (await health.CheckHealthAsync(token)).Status.ToString() : "Unspecified";
                        if (!Enum.TryParse<PluginHealthStatus>(status, out _)) status = "Unspecified";
                        var id = plugin.PluginId;
                        plugins.Add(new { id = id[..Math.Min(id.Length, 256)], order = plugin.Manifest.Order, health = status });
                    }
                    return new { items = plugins, truncated = domain.Plugins.Count > 100 };
                }
            }),
            ["drain"] = Probe(() => Task.FromResult<object?>(services.GetRequiredService<IPluginDrainDiagnostics>().GetDrainSnapshot().Take(100).Select(d => new
            {
                d.Generation, d.ActiveRequests, d.Retiring, d.RetirementSeconds,
                state = d.State is "Active" or "Draining" or "Disposing" or "Failed" ? d.State : "Unknown"
            }).ToArray())),
            ["queues"] = Probe(() => Task.FromResult<object?>(new
            {
                auditEvicted = services.GetRequiredService<IConfigChangeAuditLog>().EvictedCount,
                notifications = services.GetService<NotificationDispatcher>()?.GetSnapshot()
            })),
            ["proxy"] = Probe(() =>
            {
                var health = services.GetRequiredService<IProxyConfigErrorStore>().GetHealth();
                return Task.FromResult<object?>(new { healthy = health.ErrorCount == 0, health.ErrorCount });
            })
        };
        var completion = Task.WhenAll(probes.Values);
        _ = ReleaseAsync(completion, lifetime);
        try { await completion.WaitAsync(token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<string>();
        foreach (var (name, probe) in probes)
        {
            if (probe.IsCompletedSuccessfully) sections[name] = probe.Result;
            else failures.Add(name + (probe.IsCompleted ? ":failed" : ":timeout"));
        }
        sections["failures"] = failures;
        var json = JsonSerializer.SerializeToUtf8Bytes(sections);
        return json.Length <= MaximumBytes ? json : JsonSerializer.SerializeToUtf8Bytes(new { correlationId, failures = new[] { "size-limit" } });
    }

    private static Task<object?> Probe(Func<Task<object?>> probe) => Task.Run(probe);
    private async Task ReleaseAsync(Task completion, CancellationTokenSource lifetime)
    {
        try { await completion; }
        catch { /* Failure details are excluded from the exported bundle. */ }
        finally { lifetime.Dispose(); _gate.Release(); }
    }
}
