using Yarp.ReverseProxy.Transforms.Builder;

namespace Wafish.Plugin.RouteAuthorization;

/// <summary>Prepares immutable authorization policies while YARP builds endpoints.</summary>
public sealed class RouteAuthorizationTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }
    public void Apply(TransformBuilderContext context)
    {
        if (context.Route.Metadata?.ContainsKey(RouteAuthorizationCompiler.MetadataKey) == true)
            _ = RouteAuthorizationMiddleware.Prepare(context.Route);
    }
}
