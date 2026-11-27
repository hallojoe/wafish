using Microsoft.AspNetCore.Http;
using Wafish.Services;

namespace Wafish.Dashboard.Infrastructure.Plugin;

public sealed partial class PluginRuntimeDomainManager
{
    internal PluginRuntimeDomainLease AcquireInternalLease(HttpContext parent,out PluginRuntimeDomain domain)
    {
        lock(_lifecycleGate)
        {
            if(_stopping) throw new InternalRouteException(503,"stopping");
            domain=Current;
            if(parent.Items.TryGetValue(RequestDomainKey,out var active) && !ReferenceEquals(active,domain))
                throw new InternalRouteException(503,"runtime_transition");
            if(!domain.TryAcquire(out var lease)) throw new InternalRouteException(503,"runtime_transition");
            return lease;
        }
    }
    internal static void AttachInternalDomain(HttpContext context,PluginRuntimeDomain domain)=>context.Items[RequestDomainKey]=domain;
}
