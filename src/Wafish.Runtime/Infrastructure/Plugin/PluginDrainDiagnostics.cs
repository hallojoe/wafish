using Microsoft.Extensions.DependencyInjection;

namespace Wafish.Dashboard.Infrastructure.Plugin;

/// <summary>A domain generation without service providers or plugin-owned exception objects.</summary>
public sealed record PluginDomainDiagnostic(long Generation, int ActiveRequests, bool Retiring, double RetirementSeconds, string State);

public interface IPluginDrainDiagnostics
{
    IReadOnlyList<PluginDomainDiagnostic> GetDrainSnapshot();
}

public sealed partial class PluginRuntimeDomainManager : IPluginDrainDiagnostics
{
    private long _generation;
    private readonly Dictionary<PluginRuntimeDomain, long> _draining = [];
    private readonly Queue<PluginDomainDiagnostic> _drainFailures = [];
    private readonly TimeProvider _drainClock;
    private TimeProvider DrainClock => _drainClock;

    public IReadOnlyList<PluginDomainDiagnostic> GetDrainSnapshot()
    {
        lock (_lifecycleGate)
        {
            var result = new List<PluginDomainDiagnostic>();
            if (!_stopping) result.Add(new(Current.Generation, Current.ActiveRequests, false, 0, "Active"));
            foreach (var (domain, since) in _draining)
                result.Add(new(domain.Generation, domain.ActiveRequests, true, DrainClock.GetElapsedTime(since).TotalSeconds,
                    domain.ActiveRequests > 0 ? "Draining" : "Disposing"));
            result.AddRange(_drainFailures);
            return result;
        }
    }

    private void CompleteDrain(PluginRuntimeDomain domain, bool failed)
    {
        lock (_lifecycleGate)
        {
            if (!_draining.Remove(domain, out var since)) return;
            if (!failed) return;
            if (_drainFailures.Count == 100) _drainFailures.Dequeue();
            _drainFailures.Enqueue(new(domain.Generation, domain.ActiveRequests, true, DrainClock.GetElapsedTime(since).TotalSeconds, "Failed"));
        }
    }
}
