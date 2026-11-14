using Wafish.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Wafish.Extensions;
/// <summary>
/// The WafIsh endpoint route builder extensions.
/// </summary>

public static class WafIshEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the WafIsh grpc.
    /// </summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <returns>An IEndpointRouteBuilder.</returns>
    public static IEndpointRouteBuilder MapWafIshGrpc(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetRequiredService<GatewayControlPlaneOptions>().EnableRegistration)
        {
            endpoints.MapGrpcService<GatewayRegistryGrpcService>();
        }

        return endpoints;
    }
}
