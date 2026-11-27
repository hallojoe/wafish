using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Plugin.RouteAuthorization;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

internal static class RouteAuthorizationChecks
{
    private static void Check(bool ok,string message) => ClaimsTransformChecks.Check(ok,message);
    private static void Reject(Action action) { try { action(); } catch(ArgumentException) { return; } throw new Exception("Invalid authorization configuration accepted."); }
    public static async Task RunAsync(Func<string,Func<Task>,Task> run)
    {
        await run("Route authorization: any scope, all claims, trusted identities, and validation", () =>
        {
            var config=RouteAuthorizationCompiler.Compile(new() { AllowedScopes=["read","admin"], Claims=[new("tenant",["alpha","beta"]),new("role",["reader"])] });
            ClaimsPrincipal User(params Claim[] claims)=>new(new ClaimsIdentity(claims,"test"));
            Check(config.Allows(User(new Claim("scp","write read"),new("tenant","alpha"),new("role","reader"))),"Allowed scope denied.");
            Check(config.Allows(User(new Claim("scope","admin"),new("tenant","beta"),new("role","reader"))),"Alternative values denied.");
            Check(!config.Allows(User(new Claim("scope","READ"),new("tenant","alpha"),new("role","reader"))),"Scope comparison ignored case.");
            Check(!config.Allows(User(new Claim("scope","read"),new("tenant","alpha"))),"Missing claim accepted.");
            Check(!config.Allows(new(new ClaimsIdentity([new("scope","read"),new("tenant","alpha"),new("role","reader")]))),"Unauthenticated identity trusted.");
            Reject(()=>RouteAuthorizationCompiler.Compile(new()));
            Reject(()=>RouteAuthorizationCompiler.Compile(new() { AllowedScopes=["a b"] }));
            Reject(()=>RouteAuthorizationCompiler.Compile(new() { AllowedScopes=["a","a"] }));
            Reject(()=>RouteAuthorizationCompiler.Compile(new() { Claims=[new("role",[])] }));
            Reject(()=>RouteAuthorizationCompiler.Compile(new() { Claims=[new("role",["a"]),new("role",["b"])] }));
            Check(RouteAuthorizationCompiler.Compile(new() {Claims=[new("role",["reader"])]}).Allows(User(new Claim("role","reader"))),"Claims-only policy failed.");
            Check(RouteAuthorizationCompiler.Compile(new() {AllowedScopes=["read"]}).Allows(User(new Claim("scope","write"),new("scp","read"))),"Repeated scope policy failed.");
            var support=new RoutePolicyPipelineSupport(); support.Disable();
            Reject(()=>new RouteAuthorizationCompiler(support).Compile(new("id","route-authorization",Wafish.Storage.Entities.PluginBindingScope.Route,"r","{\"allowedScopes\":[\"read\"]}",1,1,0),new() {RouteId="r"}));
            return Task.CompletedTask;
        });
        await run("Route authorization: redirects, native policies, anonymous denial, and invalid activation",async () =>
        {
            await using var fixture=await RedirectChecks.Fixture.Start(s=>
            {
                s.AddAuthentication("claims-test").AddScheme<AuthenticationSchemeOptions,ClaimsTransformChecks.TestAuthentication>("claims-test",_=>{});
                s.AddAuthorization(o=>o.AddPolicy("native-deny",p=>p.RequireAssertion(_=>false)));
            });
            var routes=fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            foreach(var id in new[]{"access","native","anonymous"})
            {
                Check((await routes.TryAddRouteConfig(new() {RouteId=id,Match=new() {Path="/"+id},AuthorizationPolicy=id=="native"?"native-deny":id=="anonymous"?"anonymous":null})).Success,"Route failed.");
                await fixture.Bind(id,"redirect","{\"target\":\"/allowed\"}");
            }
            const string json="{\"allowedScopes\":[\"orders.read\"],\"claims\":[{\"type\":\"role\",\"allowedValues\":[\"reader\"]}]}";
            await fixture.Bind("access","route-authorization",json);
            await fixture.Bind("native","route-authorization",json);
            try { await fixture.Bind("anonymous","route-authorization",json); throw new Exception("Anonymous policy conflict accepted."); } catch(ArgumentException) {}
            await fixture.WaitRoute("access");
            using var anonymous=await fixture.Client.GetAsync("/access"); Check(anonymous.StatusCode==HttpStatusCode.Unauthorized && anonymous.Headers.Location==null,"Anonymous redirect escaped.");
            foreach(var (path,user,status) in new[]{("/access","user",307),("/access","missing",403),("/native","user",403)})
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,path); request.Headers.Add("X-Test-Identity",user);
                using var response=await fixture.Client.SendAsync(request);
                Check((int)response.StatusCode==status,"Unexpected authorization status for "+path+": "+response.StatusCode);
                if(status==403) Check(response.Headers.Location==null,"Denied request redirected.");
            }
        });
    }
}
