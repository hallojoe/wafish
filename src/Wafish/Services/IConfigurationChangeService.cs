using Wafish.Models;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>Complete desired route and cluster set. ExpectedVersion protects changes made after a preview.</summary>
public sealed record ConfigurationChangeRequest(long ExpectedVersion, IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters)
{
    public IReadOnlyDictionary<string, bool>? RouteActivation { get; init; }
}

/// <summary>A normalized identity-level difference. Configuration bodies remain in the preview candidate.</summary>
public sealed record ConfigurationChange(string Kind, string Id, string Operation);

/// <summary>Validation and normalized changes without storage or activation.</summary>
public sealed record ConfigurationPreviewResult(bool Valid, long ExpectedVersion, long ActiveVersion, long PersistedVersion,
    ConfigurationChangeRequest? Normalized, IReadOnlyList<ConfigurationChange> Changes, IReadOnlyList<string> Errors)
{
    public IReadOnlyDictionary<string, IReadOnlyList<WafRuleExclusion>> WafExclusions { get; init; } = new Dictionary<string, IReadOnlyList<WafRuleExclusion>>();
}

public interface IConfigurationChangeService
{
    Task<ConfigurationPreviewResult> PreviewConfigurationAsync(ConfigurationChangeRequest request, CancellationToken cancellationToken = default);
    Task<RouteOperationResult> ApplyConfigurationAsync(ConfigurationChangeRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
}
