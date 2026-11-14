using Wafish.Plugins;
using Microsoft.Extensions.Logging;

namespace Wafish.Dashboard.Infrastructure.Plugin;

/// <summary>A candidate plugin and its declared requirements. Null health means no probe exists.</summary>
public sealed record PluginActivationPreviewItem(string PluginId, PluginResourceRequirements Resources, PluginHealthProbeResult? Health);

/// <summary>Preparation results without publication or resource startup.</summary>
public sealed record PluginActivationPreview(bool Ready, IReadOnlyList<PluginActivationPreviewItem> Plugins, IReadOnlyList<string> Errors);

/// <summary>Prepares and disposes a candidate without replacing the current domain.</summary>
public interface IPluginActivationPreviewService
{
    Task<PluginActivationPreview> PreviewAsync(IReadOnlyCollection<string> enabledPluginIds, CancellationToken cancellationToken = default);
}

public sealed partial class PluginRuntimeDomainManager : IPluginActivationPreviewService
{
    public async Task<PluginActivationPreview> PreviewAsync(IReadOnlyCollection<string> enabledPluginIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabledPluginIds);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = enabledPluginIds.ToArray();
        if (ids.Length > 1000 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            return new(false, [], ["Supply at most 1,000 distinct, nonempty plugin IDs."]);
        var reports = new List<PluginActivationPreviewItem>();
        var errors = new List<string>();
        try
        {
            await using var preparation = await PrepareAsync(ids, cancellationToken);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!preparation.Candidate.Plugins.TryGetValue(id, out var plugin))
                {
                    errors.Add($"Plugin '{id}' could not be loaded. Check its installation and runtime logs.");
                    continue;
                }
                var health = plugin is IPluginHealthProbe probe ? await probe.CheckHealthAsync(cancellationToken) : null;
                reports.Add(new(id, plugin.Manifest.Resources, health));
                if (health?.Status is PluginHealthStatus.Unhealthy or PluginHealthStatus.Disabled)
                    errors.Add($"Plugin '{id}' is not ready: {health.Message}");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Plugin activation preview failed.");
            errors.Add("Candidate preparation, health checks, or cleanup failed. Check the runtime logs.");
        }
        return new(errors.Count == 0, reports, errors);
    }
}
