using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wafish.Plugin.ClaimsTransform;
using Wafish.Plugins;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Services;
using Wafish.Storage;
using Yarp.ReverseProxy.Configuration;

internal static class ClaimsTransformChecks
{
    internal static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid mapping accepted."); }
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Claims transform: validation, missing values, ambiguity, and bounds", () =>
        {
            var compiler = new ClaimsTransformCompiler();
            var config = ClaimsTransformCompiler.Compile(new() { PathTemplate = "/tenant/{id}", Mappings = [new("tenant", ClaimTarget.Path, "id"), new("locale", ClaimTarget.Query, "lang", false), new("sub", ClaimTarget.Header, "X-User")] });
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("tenant", "a b"), new("sub", "u")], "test"));
            var result = ClaimsTransformEvaluator.Evaluate(config, principal);
            Check(result.ErrorStatus == null && result.Path == "/tenant/a%20b" && result.Mappings[0].Value == null, "Mapping or optional removal failed.");
            Check(ClaimsTransformEvaluator.Evaluate(config, new()).ErrorStatus == 401, "Anonymous mapping accepted.");
            Check(ClaimsTransformEvaluator.Evaluate(config, new(new ClaimsIdentity([], "test"))).ErrorStatus == 403, "Required claim missing.");
            foreach (var value in new[] { "..", "/x", "\\x", "%2e", "\r\n", "", new string('x',4097) })
                Check(ClaimsTransformEvaluator.Evaluate(config, new(new ClaimsIdentity([new("tenant", value), new("sub", "u")], "test"))).ErrorStatus == 403, "Unsafe value accepted.");
            principal.AddIdentity(new ClaimsIdentity([new("tenant", "other")], "second"));
            Check(ClaimsTransformEvaluator.Evaluate(config, principal).ErrorStatus == 403, "Ambiguous identity accepted.");
            foreach (var name in new[] { "Host", "Authorization", "Cookie", "Connection", "X-Forwarded-User", "Content-Length", "bad name" })
                Reject(() => ClaimsTransformCompiler.Compile(new() { Mappings = [new("sub", ClaimTarget.Header, name)] }));
            Reject(() => ClaimsTransformCompiler.Compile(new()));
            Reject(() => ClaimsTransformCompiler.Compile(new() { Mappings = [new("a", ClaimTarget.Path, "id", false)] }));
            Reject(() => ClaimsTransformCompiler.Compile(new() { PathTemplate = "//host/{id}", Mappings = [new("a", ClaimTarget.Path, "id")] }));
            Reject(() => ClaimsTransformCompiler.Compile(new() { PathTemplate = "/{other}", Mappings = [new("a", ClaimTarget.Path, "id")] }));
            Reject(() => ClaimsTransformCompiler.Compile(new() { Mappings = [new("a", ClaimTarget.Header, "X-A"), new("b", ClaimTarget.Header, "x-a")] }));
            var binding = new PluginBindingSnapshot("b", "claims-transform", Wafish.Storage.Entities.PluginBindingScope.Route, "r", "{\"mappings\":[{\"claim\":\"sub\",\"target\":\"Header\",\"name\":\"X-User\"}]}", 1, 1, 0);
            Reject(() => compiler.Compile(binding, new() { RouteId = "r", AuthorizationPolicy = "anonymous" }));
            Reject(() => compiler.Compile(binding with { SchemaVersion = 2 }, new() { RouteId = "r" }));
            Reject(() => compiler.Compile(binding, new() { RouteId = "r", Transforms = [new Dictionary<string,string> { ["RequestHeader"]="X-User", ["Set"]="spoof" }] }));
            var total = ClaimsTransformCompiler.Compile(new() { Mappings = Enumerable.Range(0,5).Select(i => new ClaimMapping("big", ClaimTarget.Header, "X-"+i)).ToArray() });
            Check(ClaimsTransformEvaluator.Evaluate(total, new(new ClaimsIdentity([new("big", new string('x',4096))], "test"))).ErrorStatus == 403, "Combined budget bypassed.");
            return Task.CompletedTask;
        });
        await run("Claims transform: real forwarding, spoof removal, challenge, and rejected activation", async () =>
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var backend = builder.Build();
            var calls = 0;
            backend.MapGet("/{**path}", (HttpContext c) => { Interlocked.Increment(ref calls); return Results.Json(new { path = c.Request.Path.Value, user = c.Request.Headers["X-User"].ToString(), query = c.Request.QueryString.Value }); });
            await backend.StartAsync();
            await using var fixture = await RedirectChecks.Fixture.Start(s => s.AddAuthentication("claims-test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("claims-test", _ => { }));
            var config = fixture.App.Services.GetRequiredService<IDynamicYarpConfigService>();
            Check((await config.TryAddClusterConfig(new() { ClusterId="claims-backend", Destinations=new Dictionary<string,DestinationConfig> { ["one"]=new() { Address=backend.Urls.Single() } } })).Success,"Cluster failed.");
            Check((await config.TryAddRouteConfig(new() { RouteId="claims-route", ClusterId="claims-backend", Match=new() { Path="/claims" } })).Success,"Route failed.");
            var binding = await fixture.Bind("claims-route", "claims-transform", "{\"pathTemplate\":\"/tenant/{tenant}\",\"mappings\":[{\"claim\":\"tenant_id\",\"target\":\"Path\",\"name\":\"tenant\"},{\"claim\":\"sub\",\"target\":\"Header\",\"name\":\"X-User\"},{\"claim\":\"locale\",\"target\":\"Query\",\"name\":\"language\",\"required\":false}]}");
            await fixture.WaitRoute("claims-route");
            using var request = new HttpRequestMessage(HttpMethod.Get,"/claims?language=spoof&keep=ok"); request.Headers.Add("X-Test-Identity","user"); request.Headers.Add("X-User","spoof");
            using var response = await fixture.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Check(response.StatusCode==HttpStatusCode.OK,"Forwarding failed: "+body);
            using var json = JsonDocument.Parse(body);
            Check(json.RootElement.GetProperty("user").GetString()=="user" && json.RootElement.GetProperty("path").GetString()=="/tenant/alpha" && json.RootElement.GetProperty("query").GetString()=="?keep=ok", "Claim values not applied: "+body);
            using var anonymous=await fixture.Client.GetAsync("/claims");
            Check(anonymous.StatusCode==HttpStatusCode.Unauthorized && calls==1,"Anonymous request reached backend.");
            using var missingRequest=new HttpRequestMessage(HttpMethod.Get,"/claims"); missingRequest.Headers.Add("X-Test-Identity","missing");
            using var missing=await fixture.Client.SendAsync(missingRequest);
            Check(missing.StatusCode==HttpStatusCode.Forbidden && calls==1,"Missing claims reached backend.");
            var publisher=fixture.App.Services.GetRequiredService<IGatewaySnapshotPublisher>(); var version=publisher.Current.Version;
            binding.ConfigJson="{\"mappings\":[{\"claim\":\"sub\",\"target\":\"Header\",\"name\":\"Authorization\"}]}";
            try { await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(binding); throw new Exception("Invalid activation passed."); } catch (ArgumentException) { }
            Check(publisher.Current.Version==version,"Rejected configuration changed active revision.");
            Check((await config.TryAddRouteConfig(new() { RouteId="claims-redirect", Match=new() { Path="/claims-redirect" } })).Success, "Redirect route failed.");
            await fixture.Bind("claims-redirect", "claims-transform", "{\"mappings\":[{\"claim\":\"sub\",\"target\":\"Header\",\"name\":\"X-User\"}]}");
            await fixture.Bind("claims-redirect", "redirect", "{\"target\":\"/signed-in\"}");
            await fixture.WaitRoute("claims-redirect");
            using var anonymousRedirect = await fixture.Client.GetAsync("/claims-redirect");
            Check(anonymousRedirect.StatusCode == HttpStatusCode.Unauthorized, "Redirect bypassed claims authentication.");
            using var authenticatedRedirect = new HttpRequestMessage(HttpMethod.Get,"/claims-redirect");
            authenticatedRedirect.Headers.Add("X-Test-Identity","user");
            using var redirected = await fixture.Client.SendAsync(authenticatedRedirect);
            Check((int)redirected.StatusCode == 307, "Distinct endpoint policies could not compose.");
            binding.Enabled = false;
            binding.ConfigJson = "{\"mappings\":[{\"claim\":\"sub\",\"target\":\"Header\",\"name\":\"X-User\"}]}";
            await fixture.App.Services.GetRequiredService<IPluginBindingMutationService>().UpsertAsync(binding);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (fixture.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.Any(e => e.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config is { RouteId: "claims-route" } r && r.Metadata?.ContainsKey("wafish:claims-transform") == true))
                    await Task.Delay(10, timeout.Token);
            }
            using var removed = await fixture.Client.GetAsync("/claims");
            Check(removed.StatusCode == HttpStatusCode.OK && calls == 2, "Removed mapping remained active.");
            await backend.StopAsync();
        });
    }
    internal sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user=Request.Headers["X-Test-Identity"].ToString();
            if(user.Length==0) return Task.FromResult(AuthenticateResult.NoResult());
            Claim[] claims=user=="missing" ? [new("sub",user)] : [new("sub",user),new("tenant_id","alpha"),new("scope","orders.read profile"),new("role","reader")];
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name)));
        }
    }
}
