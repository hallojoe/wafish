using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wafish.Plugin.Aggregation;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Services;
using Wafish.Storage;
using Yarp.ReverseProxy.Configuration;

internal static class AggregationChecks
{
    private static void Check(bool ok,string reason)=>ClaimsTransformChecks.Check(ok,reason);
    private static void Reject(Action action) {try {action();}catch(ArgumentException){return;}throw new Exception("Invalid aggregate accepted.");}
    private static DefaultHttpContext Context()=>new() {Response={Body=new MemoryStream()}};
    private static string Body(HttpContext context)=>Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    private static AggregationOptions Options()=>new() {Children=[new("one","uid-one","/one"),new("two","uid-two","/two")]};
    public static async Task RunAsync(Func<string,Func<Task>,Task> run)
    {
        await run("Aggregation: templates, limits, headers, and immutable configuration",()=>
        {
            var options=Options();var config=AggregationCompiler.Compile(options);options.Children[0]=new("changed","other","/other");
            Check(config.Children[0].Name=="one","Compiled options were mutable.");
            foreach(var path in new[]{"https://host/path","//host","/../x","/x?y=1","/{missing}","/{route.id"})
                Reject(()=>AggregationCompiler.Compile(new() {Children=[new("a","uid",path)]}));
            Reject(()=>AggregationCompiler.Compile(new()));
            Reject(()=>AggregationCompiler.Compile(new() {Children=[new("a","uid","/"),new("a","uid2","/")]}));
            Reject(()=>AggregationCompiler.Compile(new() {Children=[new("a","uid","/",ForwardHeaders:["Host"])]}));
            Reject(()=>AggregationCompiler.Compile(new() {Children=[new("a","uid","/",Query:[new("q","x"),new("q","y")])]}));
            foreach(var setting in new Action<AggregationOptions>[] {o=>o.MaxChildren=1,o=>o.Parallelism=9,o=>o.TimeoutSeconds=61,o=>o.MaxChildBytes=4194305,o=>o.MaxCombinedBytes=16777217})
            {var invalid=Options();setting(invalid);Reject(()=>AggregationCompiler.Compile(invalid));}
            var parent=Context();parent.Request.RouteValues["id"]="a b";parent.Request.QueryString=new("?locale=en&repeated=a&repeated=b");
            var template=AggregationCompiler.Compile(new() {Children=[new("a","uid","/a/{route.id}",[new("language","{query.locale}")])]});
            var call=AggregationCompiler.Resolve(template,parent).Single();Check(call.Path=="/a/a%20b" && call.QueryString=="?language=en","Input mapping failed.");
            foreach(var value in new[]{"..","x/y","x%2fy",""})
            {parent.Request.RouteValues["id"]=value;try {AggregationCompiler.Resolve(template,parent);throw new Exception("Unsafe input accepted.");}catch(InternalRouteException e){Check(e.StatusCode==400,"Wrong input status.");}}
            var compiler=new AggregationCompiler();var binding=new PluginBindingSnapshot("b","request-aggregation",Wafish.Storage.Entities.PluginBindingScope.Route,"r",JsonSerializer.Serialize(Options()),1,1,0);
            Reject(()=>compiler.Compile(binding,new() {RouteId="r",ClusterId="c",Match=new(){Methods=["GET"]}}));
            Reject(()=>compiler.Compile(binding,new() {RouteId="r",Match=new(){Methods=["POST"]}}));
            return Task.CompletedTask;
        });
        await run("Aggregation: all-or-nothing JSON, admission, cancellation, and deadlines",async()=>
        {
            var config=AggregationCompiler.Compile(Options());
            var executor=new FakeExecutor((i,_)=>Task.FromResult(new InternalRouteResponse(i==0?200:204,"application/json",null,null,Encoding.UTF8.GetBytes(i==0?"{\"ok\":true}":""))));
            var context=Context();await new AggregationHandler(executor,new()).InvokeAsync(context,config);
            Check(Body(context)=="{\"one\":{\"ok\":true},\"two\":null}" && context.Response.Headers.CacheControl=="no-store" && executor.Disposed,"Composition or cleanup failed.");
            foreach(var (status,type,encoding,body,expected) in new[]{(403,"application/json","","secret",403),(429,"application/json","","secret",429),(504,"application/json","","secret",504),(500,"application/json","","secret",502),(200,"text/plain","","{}",502),(200,"application/json","gzip","{}",502),(200,"application/json; charset=utf-16","","{}",502),(200,"application/json","","invalid",502)})
            {
                executor=new((_,_)=>Task.FromResult(new InternalRouteResponse(status,type,encoding,null,Encoding.UTF8.GetBytes(body))));
                context=Context();await new AggregationHandler(executor,new()).InvokeAsync(context,config);
                Check(context.Response.StatusCode==expected && Body(context)=="" && executor.Disposed,"Failure exposed data or lost cleanup.");
            }
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var joined=false;
            executor=new(async(i,ct)=>
            {
                if(i==0) {await started.Task.WaitAsync(ct);return new(500,"application/json",null,null,[]);}
                started.TrySetResult();try {await Task.Delay(Timeout.Infinite,ct);}finally {joined=true;}
                throw new Exception("Unreachable");
            });
            context=Context();await new AggregationHandler(executor,new()).InvokeAsync(context,config);
            Check(context.Response.StatusCode==502 && joined && executor.Disposed,"Failed aggregate did not join its sibling.");
            var active=0;var maximum=0;
            executor=new(async(_,ct)=> {var count=Interlocked.Increment(ref active);maximum=Math.Max(maximum,count);await Task.Delay(10,ct);Interlocked.Decrement(ref active);return new(200,"application/problem+json; charset=utf-8",null,null,Encoding.UTF8.GetBytes("{}"));});
            context=Context();await new AggregationHandler(executor,new()).InvokeAsync(context,config with {Parallelism=1});
            Check(maximum==1 && context.Response.StatusCode==200,"Parallelism or JSON media type failed.");
            context=Context();context.Request.ContentLength=1;await new AggregationHandler(executor,new()).InvokeAsync(context,config);Check(context.Response.StatusCode==400,"Aggregate request body accepted.");
            var admission=new AggregationAdmission();var leases=Enumerable.Range(0,16).Select(_=>admission.TryAcquire(1)!).ToArray();
            Check(admission.TryAcquire(1)==null,"Concurrent admission limit bypassed.");
            context=Context();await new AggregationHandler(executor,admission).InvokeAsync(context,config);Check(context.Response.StatusCode==503,"Admission status failed.");
            foreach(var lease in leases){lease.Dispose();lease.Dispose();}
            using(var full=admission.TryAcquire(AggregationAdmission.MaxBufferBytes))Check(full!=null && admission.TryAcquire(1)==null,"Buffer reservation bypassed.");
            Check(admission.TryAcquire(-1)==null,"Invalid reservation accepted.");
            executor=new(async(_,ct)=>{await Task.Delay(Timeout.Infinite,ct);throw new Exception("Unreachable");});
            context=Context();await new AggregationHandler(executor,new()).InvokeAsync(context,config with {TimeoutSeconds=1});
            Check(context.Response.StatusCode==504 && executor.Disposed,"Deadline did not stop children.");
            using var canceled=new CancellationTokenSource();canceled.Cancel();context=Context();context.RequestAborted=canceled.Token;
            try {await new AggregationHandler(executor,new()).InvokeAsync(context,config);throw new Exception("Parent cancellation ignored.");}catch(OperationCanceledException){}
            executor=new((_,_)=>Task.FromResult(new InternalRouteResponse(200,"application/json",null,null,Encoding.UTF8.GetBytes("{\"size\":1}"))));
            context=Context();await new AggregationHandler(executor,new()).InvokeAsync(context,config with {MaxCombinedBytes=1});Check(context.Response.StatusCode==502 && Body(context)=="","Combined limit bypassed.");
        });
        await run("Aggregation: real parallel children, claims, dependency validation, and disabled route",async()=>
        {
            var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");await using var backend=builder.Build();
            var calls=0;var profileAttempts=0;var both=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.MapGet("/{kind}/{id}",async(HttpContext c)=>
            {
                if(Interlocked.Increment(ref calls)==2)both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10),c.RequestAborted);
                if(c.Request.RouteValues["kind"]?.ToString()=="profile" && Interlocked.Increment(ref profileAttempts)==1){c.Response.StatusCode=503;return;}
                await c.Response.WriteAsJsonAsync(new {kind=c.Request.RouteValues["kind"],id=c.Request.RouteValues["id"],user=c.Request.Headers["X-User"].ToString()});
            });await backend.StartAsync();
            await using var fixture=await RedirectChecks.Fixture.Start(s=>s.AddAuthentication("claims-test").AddScheme<AuthenticationSchemeOptions,ClaimsTransformChecks.TestAuthentication>("claims-test",_=>{}));
            var config=fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await config.TryAddClusterConfig(new() {ClusterId="aggregate-backend",Destinations=new Dictionary<string,DestinationConfig>{["one"]=new(){Address=backend.Urls.Single()}}})).Success,"Cluster failed.");
            foreach(var id in new[]{"profile","orders","combined"})
                Check((await config.TryAddRouteConfig(new(){RouteId=id,ClusterId=id=="combined"?null:"aggregate-backend",Match=new(){Path="/"+id+"/{id}",Methods=["GET"]}})).Success,"Route failed.");
            await fixture.Bind("profile","claims-transform","{\"mappings\":[{\"claim\":\"sub\",\"target\":\"Header\",\"name\":\"X-User\"}]}");
            await fixture.Bind("profile","request-retry","{\"maxRetries\":1,\"backoffBaseMs\":0,\"backoffJitterMs\":0}");
            await fixture.Bind("orders","route-authorization","{\"allowedScopes\":[\"orders.read\"]}");
            var publisher=fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>();
            var options=new AggregationOptions {Children=[new("profile",publisher.Current.RouteUidsById["profile"],"/profile/{route.id}"),new("orders",publisher.Current.RouteUidsById["orders"],"/orders/{route.id}")]};
            var binding=await fixture.Bind("combined","request-aggregation",JsonSerializer.Serialize(options,new JsonSerializerOptions(JsonSerializerDefaults.Web) {DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull}));
            await InternalRouteExecutionChecks.WaitReady(fixture);
            using(var denied=await fixture.Client.GetAsync("/combined/42"))Check(denied.StatusCode==HttpStatusCode.Unauthorized && calls==0,"Child authorization dispatched a backend.");
            using var request=new HttpRequestMessage(HttpMethod.Get,"/combined/42");request.Headers.Add("X-Test-Identity","user");
            using(var response=await fixture.Client.SendAsync(request))
            {
                var body=await response.Content.ReadAsStringAsync();Check(response.StatusCode==HttpStatusCode.OK,"Aggregate failed: "+response.StatusCode+" "+body);
                using var json=JsonDocument.Parse(body);Check(json.RootElement.GetProperty("profile").GetProperty("user").GetString()=="user" && calls==3,"Claims, retry, or parallel forwarding failed.");
                Check(response.Headers.CacheControl?.NoStore==true,"Aggregate was cacheable.");
            }
            var version=publisher.Current.Version;
            Check(!(await config.TryRemoveRoute("profile")).Success && publisher.Current.Version==version,"Referenced route deletion succeeded.");
            Check(!(await config.TryAddRouteConfig(new(){RouteId="profile",ClusterId="aggregate-backend",Match=new(){Path="/moved/{id}",Methods=["GET"]}})).Success && publisher.Current.Version==version,"Referenced route match changed without a repaired child request.");
            try {await fixture.Bind("profile","redirect","{\"target\":\"/other\"}");throw new Exception("Referenced route became a redirect.");}catch(ArgumentException){}
            try {await fixture.Bind("combined","request-retry","{}");throw new Exception("Parent retry accepted.");}catch(ArgumentException){}
            var stored=await fixture.App.Services.GetRequiredService<IPluginConfigurationRepository>().GetBindingsSnapshotAsync();
            options.Children[0]=options.Children[0] with {Path="/moved/{route.id}"};
            binding=stored.Bindings.Single(b=>b.Id==binding.Id);
            binding.ConfigJson=JsonSerializer.Serialize(options,new JsonSerializerOptions(JsonSerializerDefaults.Web) {DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull});
            var changedRoutes=config.GetRoutes().Select(r=>r.RouteId=="profile"?r with {Match=r.Match with {Path="/moved/{id}"}}:r).ToArray();
            var changeSet=new ConfigurationChangeSetRequest(new(config.GetDynamicConfig()!.Version,changedRoutes,config.GetClusters()),stored.Revision,stored.Bindings);
            var repaired=await fixture.App.Services.GetRequiredService<IConfigurationChangeSetService>().ApplyChangeSetAsync(changeSet,"aggregate-test","repair-child");
            Check(repaired.Configuration.Success,"Atomic dependency repair failed: "+repaired.Configuration.Message);
            await fixture.Bind("combined","rate-limit","{\"enabled\":true,\"permitLimit\":1,\"window\":\"1h\",\"partitionKey\":\"Route\"}");
            await InternalRouteExecutionChecks.WaitReady(fixture);
            foreach(var status in new[]{200,429})
            {
                using var quotaRequest=new HttpRequestMessage(HttpMethod.Get,"/combined/42");quotaRequest.Headers.Add("X-Test-Identity","user");
                using var quotaResponse=await fixture.Client.SendAsync(quotaRequest);Check((int)quotaResponse.StatusCode==status,"Aggregate parent quota expected "+status+", got "+quotaResponse.StatusCode+": "+await quotaResponse.Content.ReadAsStringAsync());
            }
            var finalCalls=calls;
            binding.Enabled=false;await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(binding);await InternalRouteExecutionChecks.WaitReady(fixture);
            using(var disabled=await fixture.Client.GetAsync("/combined/42"))Check(disabled.StatusCode==HttpStatusCode.ServiceUnavailable && calls==finalCalls,"Disabled aggregate forwarded.");
            Check(!(await config.TryRemoveRoute("orders")).Success,"Disabled aggregate draft lost its dependency validation.");
            await backend.StopAsync();
        });
    }
    private sealed class FakeExecutor(Func<int,CancellationToken,Task<InternalRouteResponse>> execute):IInternalRouteExecutor
    {
        public bool Disposed {get;private set;}
        public Task<IInternalRouteBatch> PrepareAsync(HttpContext parent,IReadOnlyList<InternalRouteCall> calls,int maxResponseBytes,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();return Task.FromResult<IInternalRouteBatch>(new Batch(this,execute));}
        private sealed class Batch(FakeExecutor owner,Func<int,CancellationToken,Task<InternalRouteResponse>> execute):IInternalRouteBatch
        {
            public long SnapshotVersion=>1;
            public Task<InternalRouteResponse> ExecuteAsync(int index,CancellationToken ct)=>execute(index,ct);
            public ValueTask DisposeAsync(){owner.Disposed=true;return ValueTask.CompletedTask;}
        }
    }
}
