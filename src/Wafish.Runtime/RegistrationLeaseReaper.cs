using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wafish.Services;

namespace Wafish.Runtime;

internal sealed class RegistrationLeaseReaper(DynamicYarpConfigService configuration, TimeProvider clock, ILogger<RegistrationLeaseReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var result = await configuration.ExpireInstanceLeasesAsync(stoppingToken);
                if (!result.Success) logger.LogWarning("Expired registration cleanup failed: {Status}", result.Status);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Expired registration cleanup failed."); }
        }
    }
}
