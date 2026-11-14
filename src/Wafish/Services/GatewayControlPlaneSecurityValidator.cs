using Wafish.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
namespace Wafish.Services;

internal sealed class GatewayControlPlaneOptions
{
    public bool EnableRegistration { get; set; }
}

internal sealed class GatewayControlPlaneSecurityValidator(GatewayControlPlaneOptions registration,
    IOptions<GatewayApiAuthOptions> auth, ControlPlaneAccessPolicy policy) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (registration.EnableRegistration)
        {
            policy.ValidateStartup(auth.Value.Mode == GatewayApiAuthMode.None);
        }

        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
