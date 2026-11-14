using Wafish.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Wafish.Extensions;
/// <summary>
/// The Wafish endpoint route builder extensions.
/// </summary>

public static class WafishEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Wafish grpc.
    /// </summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <returns>An IEndpointRouteBuilder.</returns>
    public static IEndpointRouteBuilder MapWafishGrpc(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetRequiredService<GatewayControlPlaneOptions>().EnableRegistration)
        {
            endpoints.MapGrpcService<GatewayRegistryGrpcService>();
        }

        return endpoints;
    }
}
