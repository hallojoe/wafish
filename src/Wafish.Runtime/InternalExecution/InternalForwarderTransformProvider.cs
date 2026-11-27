using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Wafish.Runtime.InternalExecution;

internal sealed class InternalForwarderTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }
    public void Apply(TransformBuilderContext context) => context.AddRequestTransform(request=>
    {
        if(request.HttpContext.Features.Get<InternalRouteExecutor.PinnedProxyFeature>()!=null)
            request.HttpContext.Features.Set<IForwarderErrorFeature>(null);
        return ValueTask.CompletedTask;
    });
}
