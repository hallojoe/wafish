using Wafish.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
namespace Wafish.Dashboard.Infrastructure.Auth;

internal sealed class AdministrationSecurityValidator(IOptions<DashboardOptions> options,
    ControlPlaneAccessPolicy policy, DashboardPasskeyService passkeys) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = passkeys.Enabled;
        policy.ValidateStartup(options.Value.Auth.AuthMode == DashboardAuthMode.None && options.Value.Auth.AuthorizeRequest == null);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
