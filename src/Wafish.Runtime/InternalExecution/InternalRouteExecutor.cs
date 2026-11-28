using Wafish.Plugin.ExternalLogin;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Plugin.ClaimsTransform;
using Wafish.Plugin.RouteAuthorization;
using Wafish.Services;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Forwarder;

namespace Wafish.Runtime.InternalExecution;

internal sealed class InternalRouteExecutor(IServiceProvider services, IGatewaySnapshotPublisher snapshots,
    PluginRuntimeDomainManager domains) : IInternalRouteExecutor, IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _services=services;
    private readonly PluginRuntimeDomainManager _domains=domains;
    private readonly object _gate=new();
    private readonly CancellationTokenSource _shutdown=new();
    private readonly HashSet<Batch> _batches=[];
    private Generation? _current;
    private Task? _stop;
    public Task StartAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken)=>DisposeAsync().AsTask().WaitAsync(cancellationToken);
    public async Task<IInternalRouteBatch> PrepareAsync(HttpContext parent,IReadOnlyList<InternalRouteCall> calls,int maxResponseBytes,CancellationToken cancellationToken)
    {
        if(calls.Count is <1 or >8 || maxResponseBytes is <1 or >4*1024*1024) throw new InternalRouteException(400,"invalid_batch");
        foreach(var call in calls)
            if(string.IsNullOrEmpty(call.RouteUid) || call.Path.Length>8192 || !call.Path.StartsWith('/') || call.Path.StartsWith("//") ||
                call.Path.IndexOfAny(['\\','?','#'])>=0 || call.Path.Any(char.IsControl) || call.QueryString.Length>8192 ||
                call.QueryString.Length>0 && !call.QueryString.StartsWith('?') || call.QueryString.Any(char.IsControl) || call.ForwardHeaders?.Count>32)
                throw new InternalRouteException(400,"invalid_child_request");
        calls=calls.Select(c=>c with { ForwardHeaders=c.ForwardHeaders?.ToArray() }).ToArray();
        var publication=GatewayPublicationCoordinator.For(snapshots).Gate;
        if(!await publication.WaitAsync(0,cancellationToken)) throw new InternalRouteException(503,"configuration_transition");
        Batch batch;
        try
        {
            lock(_gate)
            {
                if(_stop!=null) throw new InternalRouteException(503,"stopping");
                var snapshot=snapshots.Current;
                var endpoints=_services.GetRequiredService<EndpointDataSource>().Endpoints;
                // Routing must have applied the complete candidate before an internal batch can enter.
                var proxy=endpoints.Where(e=>e.Metadata.GetMetadata<RouteModel>()!=null).ToArray();
                var expectedRoutes=snapshot.Routes.ToDictionary(r=>r.RouteId,r=>r.Order==null?r with { Order=int.MaxValue }:r,StringComparer.Ordinal);
                var expectedClusters=snapshot.Clusters.ToDictionary(c=>c.ClusterId,StringComparer.Ordinal);
                if(snapshot.Routes.Length!=proxy.Length || proxy.Any(e=>
                {
                    var model=e.Metadata.GetMetadata<RouteModel>()!;
                    return !expectedRoutes.TryGetValue(model.Config.RouteId,out var expected) || !(model.Config with { Order=model.Config.Order ?? int.MaxValue }).Equals(expected) ||
                        model.Cluster is { } cluster && (!expectedClusters.TryGetValue(cluster.ClusterId,out var config) || !(cluster.Model.Config with { Destinations=cluster.Destinations.ToDictionary(d=>d.Key,d=>d.Value.Model.Config) }).Equals(config));
                })) throw new InternalRouteException(503,"routing_transition");
                if(parent.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is { } parentRoute &&
                    (!expectedRoutes.TryGetValue(parentRoute.Config.RouteId,out var expectedParent) || !(parentRoute.Config with { Order=parentRoute.Config.Order ?? int.MaxValue }).Equals(expectedParent)))
                    throw new InternalRouteException(503,"routing_transition");
                var lease=_domains.AcquireInternalLease(parent,out var domain);
                try
                {
                    if(_current?.Snapshot!=snapshot)
                    {
                        var next=new Generation(_services,snapshot,endpoints);
                        var previous=_current; _current=next; previous?.Release();
                    }
                    _current.Acquire();
                    try { batch=new Batch(this,_current,domain,lease,parent,calls,maxResponseBytes,cancellationToken); }
                    catch { _current.Release();throw; }
                    _batches.Add(batch);
                }
                catch { lease.Dispose(); throw; }
            }
        }
        finally { publication.Release(); }
        try { await batch.PrepareAsync(); return batch; }
        catch { await batch.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync()
    {
        lock(_gate) return new(_stop ??= StopCoreAsync());
    }
    private async Task StopCoreAsync()
    {
        await Task.Yield();
        List<Exception> errors=[];
        try { await _shutdown.CancelAsync(); } catch(Exception error) { errors.Add(error); }
        Batch[] batches;
        lock(_gate) batches=_batches.ToArray();
        foreach(var batch in batches) { try { await batch.DisposeAsync(); } catch(Exception error) { errors.Add(error); } }
        lock(_gate) { _current?.Release(); _current=null; }
        _shutdown.Dispose();
        if(errors.Count>0) throw new AggregateException(errors);
    }
    private sealed class Generation : IDisposable
    {
        private int _references=1;
        public GatewaySnapshot Snapshot { get; }
        public FrozenRouteMatcher Matcher { get; }
        public RequestDelegate ExecutePolicies { get; }
        public Dictionary<string,CapturedRoute> Routes { get; }=new(StringComparer.Ordinal);
        public Generation(IServiceProvider host,GatewaySnapshot snapshot,IReadOnlyList<Endpoint> endpoints)
        {
            Snapshot=snapshot;
            var pipeline=new ApplicationBuilder(host);
            pipeline.UseRequestTimeouts(); pipeline.UseRateLimiter();
            pipeline.Run(context=>context.GetEndpoint()!.RequestDelegate!(context));
            ExecutePolicies=pipeline.Build();
            Matcher=new(host,endpoints);
            var clusters=new Dictionary<ClusterState,(ClusterModel Model,CapturedDestination[] Destinations)>();
            foreach(var endpoint in endpoints)
            {
                if(endpoint.Metadata.GetMetadata<RouteModel>() is not { Cluster: { } cluster } route) continue;
                var uid=snapshot.RouteUidsById.GetValueOrDefault(route.Config.RouteId);
                if(uid==null) continue;
                if(!clusters.TryGetValue(cluster,out var captured))
                {
                    var available=cluster.DestinationsState.AvailableDestinations.Select(d=>d.DestinationId).ToHashSet(StringComparer.Ordinal);
                    var destinations=cluster.Destinations.Values.Select(d=>new CapturedDestination(d,new(d.DestinationId,d.Model),available.Contains(d.DestinationId))).ToArray();
                    captured=(cluster.Model,destinations);clusters.Add(cluster,captured);
                }
                Routes[uid]=new(endpoint,route,cluster,captured.Model,captured.Destinations);
            }
        }
        public void Acquire()=>Interlocked.Increment(ref _references);
        public void Release() { if(Interlocked.Decrement(ref _references)==0) Dispose(); }
        public void Dispose()=>Matcher.Dispose();
    }
    private sealed record CapturedDestination(DestinationState Source,DestinationState Frozen,bool Available);
    private sealed record CapturedRoute(Endpoint Endpoint,RouteModel Route,ClusterState SourceCluster,ClusterModel Cluster,CapturedDestination[] Destinations)
    {
        public IReverseProxyFeature CreateFeature()
        {
            var available=new List<DestinationState>();
            var live=SourceCluster.DestinationsState.AvailableDestinations;
            foreach(var d in Destinations)
            {
                // Follow native health availability only while the source still represents this captured destination.
                var unchanged=ReferenceEquals(SourceCluster.Model,Cluster) && ReferenceEquals(d.Source.Model,d.Frozen.Model) &&
                    SourceCluster.Destinations.TryGetValue(d.Source.DestinationId,out var current) && ReferenceEquals(current,d.Source);
                if(unchanged)
                {
                    d.Frozen.Health.Active=d.Source.Health.Active;
                    d.Frozen.Health.Passive=d.Source.Health.Passive;
                }
                if(unchanged ? live.Contains(d.Source) : d.Available) available.Add(d.Frozen);
            }
            return new ReverseProxyFeature {
                Route=Route,Cluster=Cluster,AllDestinations=Destinations.Select(d=>d.Frozen).ToArray(),
                AvailableDestinations=available
            };
        }
    }
    internal sealed record PinnedProxyFeature(IReverseProxyFeature Feature);
    internal static void RestoreProxyFeature(HttpContext context)
    {
        if(context.Features.Get<PinnedProxyFeature>() is { } pinned) context.Features.Set(pinned.Feature);
    }
    private sealed class Batch : IInternalRouteBatch
    {
        private readonly InternalRouteExecutor _owner;
        private readonly Generation _generation;
        private readonly PluginRuntimeDomain _domain;
        private readonly PluginRuntimeDomainLease _lease;
        private readonly HttpContext _parent;
        private readonly IReadOnlyList<InternalRouteCall> _calls;
        private readonly int _maxBytes;
        private readonly CancellationTokenSource _cancel;
        private readonly List<Child> _children=[];
        private readonly object _gate=new();
        private readonly List<Task> _running=[];
        private Task? _dispose;
        private Task? _preparation;
        private readonly ActivityContext _activity;
        public long SnapshotVersion=>_generation.Snapshot.Version;
        public Batch(InternalRouteExecutor owner,Generation generation,PluginRuntimeDomain domain,PluginRuntimeDomainLease lease,HttpContext parent,IReadOnlyList<InternalRouteCall> calls,int maxBytes,CancellationToken cancellationToken)
        {
            _owner=owner;_generation=generation;_domain=domain;_lease=lease;_parent=parent;_calls=calls;_maxBytes=maxBytes;
            _cancel=CancellationTokenSource.CreateLinkedTokenSource(parent.RequestAborted,cancellationToken,owner._shutdown.Token);
            _activity=Activity.Current?.Context ?? default;
        }
        public Task PrepareAsync()
        {
            lock(_gate)
            {
                if(_dispose!=null) throw new InternalRouteException(503,"stopping");
                return _preparation ??= PrepareCoreAsync();
            }
        }
        private async Task PrepareCoreAsync()
        {
            await Task.Yield();
            foreach(var call in _calls)
            {
                _cancel.Token.ThrowIfCancellationRequested();
                if(!_generation.Routes.TryGetValue(call.RouteUid,out var route) || route.Route.Config.Metadata?.ContainsKey("wafish:request-aggregation")==true ||
                    _generation.Snapshot.RouteExecutionPlans[route.Route.Config.RouteId].TryGet("redirect",out _)) throw new InternalRouteException(400,"invalid_child_route");
                var scope=_owner._services.CreateAsyncScope();
                var response=new BoundedInternalResponse(_maxBytes);
                var context=new DefaultHttpContext { RequestServices=scope.ServiceProvider, User=new ClaimsPrincipal(_parent.User.Identities.Select(i=>new ClaimsIdentity(i))), RequestAborted=_cancel.Token };
                context.Features.Set(_parent.Features.Get<Wafish.Plugins.GatewayVisitorIdentityFeature>());
                context.Features.Set<IHttpResponseFeature>(response); context.Features.Set<IHttpResponseBodyFeature>(response);
                context.Request.Method="GET";context.Request.Scheme=_parent.Request.Scheme;context.Request.Host=_parent.Request.Host;
                context.Request.Path=PathString.FromUriComponent(call.Path);context.Request.PathBase=_parent.Request.PathBase;context.Request.QueryString=new(call.QueryString);
                context.Request.Protocol=_parent.Request.Protocol;
                context.Features.Get<IHttpRequestFeature>()!.RawTarget=call.Path+call.QueryString;
                context.Connection.RemoteIpAddress=_parent.Connection.RemoteIpAddress;context.Connection.LocalIpAddress=_parent.Connection.LocalIpAddress;
                context.Connection.LocalPort=_parent.Connection.LocalPort;context.Connection.RemotePort=_parent.Connection.RemotePort;
                context.TraceIdentifier=_parent.TraceIdentifier+":"+_children.Count;
                foreach(var header in new[]{"Authorization","Cookie"}.Concat(call.ForwardHeaders ?? []))
                {
                    if(header.Equals("Host",StringComparison.OrdinalIgnoreCase) || header.StartsWith("X-Forwarded-",StringComparison.OrdinalIgnoreCase) ||
                        new[]{"Connection","Content-Length","Transfer-Encoding","Forwarded","Accept-Encoding","Range","If-Range","If-Match","If-None-Match","If-Modified-Since","If-Unmodified-Since"}.Contains(header,StringComparer.OrdinalIgnoreCase)) continue;
                    if(_parent.Request.Headers.TryGetValue(header,out var value)) context.Request.Headers[header]=value;
                }
                context.Request.Headers.Accept="application/json";context.Request.Headers.AcceptEncoding="identity";
                PluginRuntimeDomainManager.AttachInternalDomain(context,_domain);
                var child=new Child(context,response,scope,route);_children.Add(child);
                await Isolated(context,async()=>
                {
                    await _generation.Matcher.MatchAsync(context);
                    if(!ReferenceEquals(context.GetEndpoint(),route.Endpoint)) throw new InternalRouteException(400,"child_route_mismatch");
                    if (route.Route.Config.Metadata?.ContainsKey(ExternalLoginCompiler.MetadataKey) == true)
                    {
                        var visitor = context.RequestServices.GetService<ExternalLoginRuntime>();
                        var visitorAllowed = false;
                        if (visitor != null) await visitor.AuthorizeAsync(context, _ => { visitorAllowed = true; return Task.CompletedTask; }, internalRequest: true);
                        if (!visitorAllowed) throw new InternalRouteException(context.Response.StatusCode == 200 ? 403 : context.Response.StatusCode, "child_external_login");
                    }
                    var policyProvider=context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
                    var policy=await AuthorizationPolicy.CombineAsync(policyProvider,route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),route.Endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
                    if(policy!=null && route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>()==null)
                    {
                        var evaluator=context.RequestServices.GetRequiredService<IPolicyEvaluator>();
                        var authentication=await evaluator.AuthenticateAsync(policy,context);
                        var result=await evaluator.AuthorizeAsync(policy,authentication,context,context);
                        if(!result.Succeeded) throw new InternalRouteException(result.Challenged?401:403,"child_authorization");
                    }
                    var allowed=false;
                    await RouteAuthorizationMiddleware.InvokeAsync(context,_=> { allowed=true;return Task.CompletedTask; });
                    if(!allowed) throw new InternalRouteException(context.User.Identities.Any(i=>i.IsAuthenticated)?403:401,"child_authorization");
                    allowed=false;
                    await ClaimsTransformProvider.InvokeAsync(context,_=> { allowed=true;return Task.CompletedTask; });
                    if(!allowed) throw new InternalRouteException(context.User.Identities.Any(i=>i.IsAuthenticated)?403:401,"child_claims");
                });
            }
        }
        public Task<InternalRouteResponse> ExecuteAsync(int index,CancellationToken cancellationToken)
        {
            lock(_gate)
            {
                if(_dispose!=null || index<0 || index>=_children.Count || _children[index].Started) throw new InvalidOperationException("Child execution is unavailable.");
                var child=_children[index];child.Started=true;
                var task=ExecuteCoreAsync(child,cancellationToken);_running.Add(task);return task;
            }
        }
        private async Task<InternalRouteResponse> ExecuteCoreAsync(Child child,CancellationToken cancellationToken)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token,cancellationToken);
            child.Context.RequestAborted=linked.Token;
            await Isolated(child.Context,async()=>
            {
                linked.Token.ThrowIfCancellationRequested();
                child.Context.Features.Set(new PinnedProxyFeature(child.Route.CreateFeature()));
                await _owner._domains.InvokeMiddlewareAsync(child.Context,_generation.ExecutePolicies);
                linked.Token.ThrowIfCancellationRequested();
                await child.Response.CompleteAsync();
                if(child.Context.Features.Get<IForwarderErrorFeature>() is { Error: not ForwarderError.None }) throw new InternalRouteException(502,"forwarding_failed");
                if(child.Response.LimitExceeded) throw new InternalRouteException(502,"response_limit");
            });
            return new(child.Response.StatusCode,child.Response.Headers.ContentType.ToString(),child.Response.Headers.ContentEncoding.ToString(),child.Response.Headers.RetryAfter.ToString(),child.Response.ToArray());
        }
        private Task Isolated(HttpContext context,Func<Task> action)
        {
            // HttpContextAccessor uses a mutable AsyncLocal holder. Do not clear the parent's holder in parallel children.
            using(ExecutionContext.SuppressFlow()) return Task.Run(async()=>
            {
                var accessor=context.RequestServices.GetService<IHttpContextAccessor>();
                if(accessor!=null) accessor.HttpContext=context;
                using var snapshot=RequestExecutionSnapshot.Enter(_generation.Snapshot);
                using var activity=new Activity("wafish.internal-route");
                if(_activity.TraceId!=default) activity.SetParentId(_activity.TraceId,_activity.SpanId,_activity.TraceFlags);
                activity.Start();
                try { await action(); }
                finally { if(accessor!=null) accessor.HttpContext=null; }
            });
        }
        public ValueTask DisposeAsync() { lock(_gate) return new(_dispose ??= DisposeCoreAsync()); }
        private async Task DisposeCoreAsync()
        {
            await Task.Yield();
            List<Exception> errors=[];
            try { await _cancel.CancelAsync(); } catch(Exception error) { errors.Add(error); }
            Task? preparation; lock(_gate) preparation=_preparation;
            if(preparation!=null) { try { await preparation; } catch { } }
            Task[] pending;lock(_gate)pending=_running.ToArray();
            try { await Task.WhenAll(pending); } catch { /* Each returned execution task retains its original failure. */ }
            try
            {
                foreach(var child in _children)
                {
                    try { await child.Response.CompleteAsync(); } catch(Exception error) { errors.Add(error); }
                    try { child.Response.Dispose(); } catch(Exception error) { errors.Add(error); }
                    try { await child.Scope.DisposeAsync(); } catch(Exception error) { errors.Add(error); }
                }
                if(errors.Count>0) throw new AggregateException(errors);
            }
            finally { _cancel.Dispose();_lease.Dispose();_generation.Release();lock(_owner._gate)_owner._batches.Remove(this); }
        }
        private sealed class Child(HttpContext context,BoundedInternalResponse response,AsyncServiceScope scope,CapturedRoute route)
        {
            public HttpContext Context { get; }=context;
            public BoundedInternalResponse Response { get; }=response;
            public AsyncServiceScope Scope { get; }=scope;
            public CapturedRoute Route { get; }=route;
            public bool Started { get; set; }
        }
    }
}
