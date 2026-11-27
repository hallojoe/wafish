using System.Net;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

internal static class InternalRouteExecutionChecks
{
    private static void Check(bool ok,string reason)=>ClaimsTransformChecks.Check(ok,reason);
    internal static async Task WaitReady(RedirectChecks.Fixture fixture)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true)
        {
            var snapshot=fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>().Current;
            var models=fixture.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.Select(e=>e.Metadata.GetMetadata<RouteModel>()).OfType<RouteModel>().ToArray();
            if(models.Length==snapshot.Routes.Length && models.All(m=>snapshot.Routes.Any(r=>(r with {Order=r.Order ?? int.MaxValue}).Equals(m.Config with { Order=m.Config.Order ?? int.MaxValue })) && (m.Cluster==null || snapshot.Clusters.Any(c=>c.Equals(m.Cluster.Model.Config with { Destinations=m.Cluster.Destinations.ToDictionary(d=>d.Key,d=>d.Value.Model.Config) }))))) return;
            await Task.Delay(10,timeout.Token);
        }
    }
    public static async Task RunAsync(Func<string,Func<Task>,Task> run)
    {
        var runner=run;run=(name,action)=>runner(name,async()=> {try {await action();}catch(Exception error){Console.Error.WriteLine(error);throw;} });
        await run("Internal responses: callbacks, pipes, files, and limits",async()=>
        {
            using var response=new Wafish.Runtime.InternalExecution.BoundedInternalResponse(32);
            var order=new List<int>();
            response.OnStarting(_=>{order.Add(1);return Task.CompletedTask;},new());
            response.OnStarting(_=>{order.Add(2);return Task.CompletedTask;},new());
            response.OnCompleted(_=>{order.Add(3);return Task.CompletedTask;},new());
            await response.Writer.WriteAsync(Encoding.UTF8.GetBytes("hello"));
            await response.CompleteAsync();await response.CompleteAsync();
            Check(order.SequenceEqual(new[]{2,1,3}) && Encoding.UTF8.GetString(response.ToArray())=="hello","Response lifecycle failed.");
            try {response.OnStarting(_=>Task.CompletedTask,new());throw new Exception("Late callback accepted.");}catch(InvalidOperationException){}
            var path=Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(path,"abcdef");
                using var file=new Wafish.Runtime.InternalExecution.BoundedInternalResponse(8);
                await file.SendFileAsync(path,1,3);Check(Encoding.UTF8.GetString(file.ToArray())=="bcd","SendFile bounds failed.");
                try {await file.WriteAsync(new byte[8]);throw new Exception("Overflow accepted.");}catch(InternalRouteException){}
                Check(file.LimitExceeded,"Overflow lost.");
            }
            finally {File.Delete(path);}
            using var failed=new Wafish.Runtime.InternalExecution.BoundedInternalResponse(8);
            var completed=false;
            failed.OnCompleted(_=>throw new InvalidOperationException(),new());
            failed.OnCompleted(_=>{completed=true;return Task.CompletedTask;},new());
            try {await failed.CompleteAsync();throw new Exception("Callback failure lost.");}catch(AggregateException){}
            Check(completed,"Callback cleanup skipped.");
            var snapshot=GatewaySnapshot.Empty;
            using(RequestExecutionSnapshot.Enter(snapshot)) Check(ReferenceEquals(RequestExecutionSnapshot.Current,snapshot),"Snapshot context missing.");
            Check(RequestExecutionSnapshot.Current==null,"Snapshot context leaked.");
        });
        await run("Internal routes: policy preflight, parallel isolation, bounded responses, and snapshot retention",async()=>
        {
            var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var backend=builder.Build();
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls=0;
            backend.MapGet("/{**path}",async (HttpContext c)=>
            {
                Interlocked.Increment(ref calls);
                if(c.Request.Path=="/slow") { entered.TrySetResult();await release.Task.WaitAsync(c.RequestAborted); }
                if(c.Request.Path=="/large") { await c.Response.WriteAsync(new string('x',4096));return; }
                if(c.Request.Path=="/broken") { c.Response.ContentLength=100;await c.Response.WriteAsync("{}");await c.Response.Body.FlushAsync();c.Abort();return; }
                await c.Response.WriteAsJsonAsync(new {path=c.Request.Path.Value,user=c.Request.Headers["X-User"].ToString(),query=c.Request.QueryString.Value});
            });
            await backend.StartAsync();
            await using var fixture=await RedirectChecks.Fixture.Start(s=>
            {
                s.AddAuthentication("claims-test").AddScheme<AuthenticationSchemeOptions,ClaimsTransformChecks.TestAuthentication>("claims-test",_=>{});
                s.AddAuthorization(o=>o.AddPolicy("denied",p=>p.RequireAssertion(_=>false)));
                s.AddHttpContextAccessor();
                s.AddRateLimiter(o=> {o.RejectionStatusCode=429;o.AddFixedWindowLimiter("internal-one",l=>{l.PermitLimit=1;l.Window=TimeSpan.FromHours(1);l.QueueLimit=0;});});
                s.AddSingleton<IRoutePluginCompiler,GraphValidator>();
            });
            var config=fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await config.TryAddClusterConfig(new() {ClusterId="internal",Destinations=new Dictionary<string,DestinationConfig> { ["one"]=new() {Address=backend.Urls.Single()} }})).Success,"Cluster failed.");
            foreach(var id in new[]{"one","two","large","broken","slow","denied","limited","waf"})
                Check((await config.TryAddRouteConfig(new() {RouteId=id,ClusterId="internal",Match=new() {Path="/"+id,Methods=["GET"]},AuthorizationPolicy=id=="denied"?"denied":null,RateLimiterPolicy=id=="limited"?"internal-one":null})).Success,"Route failed.");
            await fixture.Bind("one","claims-transform","{\"mappings\":[{\"claim\":\"sub\",\"target\":\"Header\",\"name\":\"X-User\"}]}");
            await fixture.Bind("one","route-authorization","{\"allowedScopes\":[\"read\"]}");
            await fixture.Bind("waf","waf","{\"enabled\":true,\"ipBlacklist\":[\"127.0.0.1\"]}");
            await WaitReady(fixture);
            var publisher=fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            string Uid(string id)=>publisher.Current.RouteUidsById[id];
            var executor=fixture.App.Services.GetRequiredService<IInternalRouteExecutor>();
            var parent=new DefaultHttpContext {RequestServices=fixture.App.Services,User=new(new ClaimsIdentity([new("sub","trusted"),new("scope","read")],"test"))};
            parent.Request.Scheme="http";parent.Request.Host=new("example.test");parent.Request.Protocol="HTTP/1.1";
            parent.Request.Headers["X-User"]="spoof";
            var accessor=fixture.App.Services.GetRequiredService<IHttpContextAccessor>();accessor.HttpContext=parent;
            async Task Reject(InternalRouteCall[] children,int status,int limit=1024,bool execute=false)
            {
                try { await using var batch=await executor.PrepareAsync(parent,children,limit,default);if(execute) await batch.ExecuteAsync(0,default);throw new Exception("Invalid child accepted."); }
                catch(InternalRouteException e) { Check(e.StatusCode==status,$"Expected {status}, got {e.StatusCode}: {e.Code}"); }
            }
            await Reject([new(Uid("one"),"//other")],400);
            await Reject([new(Uid("one"),"/two")],400);
            await Reject([new(Uid("one"),"/ops")],400);
            await Reject([new("missing","/one")],400);
            await Reject([new(Uid("two"),"/two"),new(Uid("denied"),"/denied")],403);
            Check(calls==0,"Preflight dispatched a child.");
            var user=parent.User;parent.User=new();await Reject([new(Uid("one"),"/one")],401);parent.User=user;
            await using(var batch=await executor.PrepareAsync(parent,[new(Uid("one"),"/one","?id=1",["X-User"]),new(Uid("two"),"/two","?id=2")],1024,default))
            {
                var responses=await Task.WhenAll(batch.ExecuteAsync(0,default),batch.ExecuteAsync(1,default));
                Check(responses.All(r=>r.StatusCode==200),"Internal forwarding failed.");
                Check(Encoding.UTF8.GetString(responses[0].Body).Contains("trusted") && Encoding.UTF8.GetString(responses[1].Body).Contains("id=2"),"Child requests were mixed.");
                Check(ReferenceEquals(accessor.HttpContext,parent) && parent.Request.Headers["X-User"]=="spoof","Parent context changed.");
                try { await batch.ExecuteAsync(0,default);throw new Exception("Child executed twice."); } catch(InvalidOperationException) {}
            }
            foreach(var status in new[]{200,429})
            {
                await using var quota=await executor.PrepareAsync(parent,[new(Uid("limited"),"/limited")],1024,default);
                Check((await quota.ExecuteAsync(0,default)).StatusCode==status,"Native child quota bypassed.");
            }
            parent.Connection.RemoteIpAddress=IPAddress.Loopback;
            await using(var waf=await executor.PrepareAsync(parent,[new(Uid("waf"),"/waf")],8192,default))
                Check((await waf.ExecuteAsync(0,default)).StatusCode==403,"Child WAF bypassed.");
            var before=publisher.Current.Version;
            Check(!(await config.TryAddRouteConfig(new() {RouteId="invalid-graph",Match=new() {Path="/invalid"}})).Success && publisher.Current.Version==before,"Graph validation published an invalid route.");
            await Reject([new(Uid("large"),"/large")],502,64,true);
            await Reject([new(Uid("broken"),"/broken")],502,1024,true);
            await using(var batch=await executor.PrepareAsync(parent,[new(Uid("two"),"/two")],1024,default))
            {
                var version=batch.SnapshotVersion;
                Check((await config.TryUpdateCluster("internal",new() {Destinations=new() { ["one"]="http://127.0.0.1:1" }})).Success,"Cluster update failed.");
                await WaitReady(fixture);
                Check(publisher.Current.Version>version,"Snapshot did not advance.");
                var result=await batch.ExecuteAsync(0,default);Check(result.StatusCode==200,"Prepared batch lost its destination.");
            }
            Check((await config.TryUpdateCluster("internal",new() {Destinations=new() { ["one"]=backend.Urls.Single() }})).Success,"Restore failed.");await WaitReady(fixture);
            await using(var batch=await executor.PrepareAsync(parent,[new(Uid("slow"),"/slow")],1024,default))
            {
                using var cancel=new CancellationTokenSource();var pending=batch.ExecuteAsync(0,cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));cancel.Cancel();
                try { await pending;throw new Exception("Cancellation ignored."); } catch(OperationCanceledException) {}
            }
            release.TrySetResult();accessor.HttpContext=null;
            await using(var idle=await executor.PrepareAsync(parent,[new(Uid("two"),"/two")],1024,default))
            {
                await ((IAsyncDisposable)executor).DisposeAsync();
                try {await idle.ExecuteAsync(0,default);throw new Exception("Stopped batch remained executable.");}catch(InvalidOperationException){}
                await Reject([new(Uid("two"),"/two")],503);
            }
            await backend.StopAsync();
        });
    }
    private sealed class GraphValidator : IRoutePluginCompiler,IGatewaySnapshotValidator
    {
        public bool CanCompile(string id)=>false;
        public CompiledRoutePlugin Compile(PluginBindingSnapshot binding,RouteConfig route)=>throw new NotSupportedException();
        public void Validate(GatewaySnapshot snapshot)
        {if(snapshot.Routes.Any(r=>r.RouteId=="invalid-graph")) throw new ArgumentException("Invalid test dependency graph.");}
    }
}
