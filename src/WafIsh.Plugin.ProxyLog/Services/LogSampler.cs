using WafIsh.Infrastructure.Middleware;
using Microsoft.Extensions.Options;

namespace WafIsh.Plugin.ProxyLog.Services;

/// <summary>
/// Sampling decision engine. Randomness controls log volume, not security decisions.
/// </summary>
public sealed class LogSampler : ILogSampler
{
    private readonly ProxyLogRuntimeSettings _runtimeSettings;

    public LogSampler(ProxyLogRuntimeSettings runtimeSettings)
    {
        _runtimeSettings = runtimeSettings;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2245",
        Justification = "Statistical log sampling requires no cryptographic unpredictability. It does not generate credentials or authorize requests.")]
    public bool ShouldSample()
    {
        var settings = _runtimeSettings.Current;
        if (!settings.SamplingEnabled || settings.SamplingRate >= 1.0)
        {
            return true;
        }

        return settings.SamplingRate > 0 && Random.Shared.NextDouble() < settings.SamplingRate;
    }
}
