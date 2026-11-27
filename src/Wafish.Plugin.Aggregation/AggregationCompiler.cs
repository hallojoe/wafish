using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.Aggregation;

public sealed record AggregateQuery(string Name,string Value);
public sealed record AggregateChild(string Name,string RouteUid,string Path,AggregateQuery[]? Query=null,string[]? ForwardHeaders=null);
public sealed class AggregationOptions
{
    public AggregateChild[] Children { get; set; }=[];
    public int MaxChildren { get; set; }=4;
    public int Parallelism { get; set; }=4;
    public int TimeoutSeconds { get; set; }=10;
    public int MaxChildBytes { get; set; }=1024*1024;
    public int MaxCombinedBytes { get; set; }=4*1024*1024;
}
public sealed record CompiledAggregateChild(string Name,string RouteUid,string Path,ImmutableArray<AggregateQuery> Query,ImmutableArray<string> ForwardHeaders);
public sealed record CompiledAggregation(ImmutableArray<CompiledAggregateChild> Children,int Parallelism,int TimeoutSeconds,int MaxChildBytes,int MaxCombinedBytes);
public sealed partial class AggregationCompiler(RoutePolicyPipelineSupport? support=null,IGatewaySnapshotPublisher? snapshots=null) : IRoutePluginCompiler,IGatewaySnapshotValidator
{
    public const string MetadataKey="wafish:request-aggregation";
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web) {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    [GeneratedRegex(@"\{(route|query)\.([A-Za-z0-9_-]+)\}",RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    public bool CanCompile(string id)=>id.Equals("request-aggregation",StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding,RouteConfig route)
    {
        support?.RequireEnabled();
        if(binding.SchemaVersion!=1) throw new ArgumentException("Unsupported aggregation schema.");
        if(!string.IsNullOrEmpty(route.ClusterId) || route.Match.Methods is not {Count:1} || route.Match.Methods[0]!="GET")
            throw new ArgumentException("Aggregate routes require GET only and no cluster.");
        if(route.OutputCachePolicy!=null && !route.OutputCachePolicy.Equals("disable",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Aggregate response caching is unsupported.");
        var compiled=Compile(JsonSerializer.Deserialize<AggregationOptions>(binding.ConfigJson,Json) ?? throw new ArgumentException("Aggregation configuration is required."));
        var metadata=new Dictionary<string,string>(route.Metadata ?? new Dictionary<string,string>()) { [MetadataKey]=binding.ConfigJson };
        return new(binding,PluginExecutionStage.PreProxy,compiled,route with {Metadata=metadata});
    }
    public static CompiledAggregation Compile(AggregationOptions options)
    {
        if(options.MaxChildren is <1 or >8 || options.Children is null || options.Children.Length<1 || options.Children.Length>options.MaxChildren ||
            options.Parallelism is <1 or >8 || options.TimeoutSeconds is <1 or >60 || options.MaxChildBytes is <1 or >4*1024*1024 || options.MaxCombinedBytes is <1 or >16*1024*1024)
            throw new ArgumentException("Invalid aggregation limits.");
        var names=new HashSet<string>(StringComparer.Ordinal);var children=ImmutableArray.CreateBuilder<CompiledAggregateChild>();
        foreach(var child in options.Children)
        {
            if(child==null || !Name(child.Name) || !names.Add(child.Name) || string.IsNullOrWhiteSpace(child.RouteUid) || child.RouteUid.Length>256)
                throw new ArgumentException("Children require unique names and a route UID.");
            ValidateTemplate(child.Path,true);
            var query=child.Query ?? [];var headers=child.ForwardHeaders ?? [];
            if(query.Length>32 || headers.Length>32 || query.Any(q=>q==null || !Name(q.Name)) || query.Select(q=>q.Name).Distinct(StringComparer.Ordinal).Count()!=query.Length)
                throw new ArgumentException("Invalid child query mappings.");
            foreach(var q in query) ValidateTemplate(q.Value,false);
            if(headers.Any(h=>!SafeHeader(h)) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=headers.Length)
                throw new ArgumentException("Invalid child header allowlist.");
            children.Add(new(child.Name,child.RouteUid,child.Path,query.ToImmutableArray(),headers.ToImmutableArray()));
        }
        return new(children.ToImmutable(),options.Parallelism,options.TimeoutSeconds,options.MaxChildBytes,options.MaxCombinedBytes);
    }
    public void Validate(GatewaySnapshot snapshot)
    {
        var ids=snapshot.RouteUidsById.ToDictionary(p=>p.Value,p=>p.Key,StringComparer.Ordinal);
        foreach(var plan in snapshot.RouteExecutionPlans.Values)
        {
            var aggregate=plan.Plugins.FirstOrDefault(p=>CanCompile(p.Binding.PluginId));if(aggregate==null) continue;
            if(snapshot.RoutePlugins.GetValueOrDefault(plan.RouteId,[]).Any(p=>new[]{"redirect","request-retry","response-cache","claims-transform"}.Contains(p.PluginId,StringComparer.OrdinalIgnoreCase)) ||
                plan.Plugins.Any(p=>new[]{"redirect","request-retry","response-cache","claims-transform"}.Contains(p.Binding.PluginId,StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("Aggregate parents cannot use redirects, retries, response caching, or claims transforms.");
            foreach(var child in aggregate.GetRuntimeConfig<CompiledAggregation>().Children)
            {
                if(!ids.TryGetValue(child.RouteUid,out var target) || target==plan.RouteId || !snapshot.RouteExecutionPlans.TryGetValue(target,out var targetPlan))
                    throw new ArgumentException("An aggregate child route is unavailable or references itself.");
                var route=snapshot.Routes.Single(r=>r.RouteId==target);
                var previous=snapshots?.Current;
                if(previous!=null && snapshot.RouteUidsById.TryGetValue(plan.RouteId,out var parentUid))
                {
                    var oldParent=previous.RouteUidsById.FirstOrDefault(p=>p.Value==parentUid).Key;
                    var oldTarget=previous.RouteUidsById.FirstOrDefault(p=>p.Value==child.RouteUid).Key;
                    var oldRoute=previous.Routes.FirstOrDefault(r=>r.RouteId==oldTarget);
                    if(oldRoute!=null && !oldRoute.Match.Equals(route.Match) && oldParent!=null && previous.RouteExecutionPlans.TryGetValue(oldParent,out var oldPlan))
                    {
                        var oldAggregate=oldPlan.Plugins.FirstOrDefault(p=>CanCompile(p.Binding.PluginId));
                        var oldChild=oldAggregate?.GetRuntimeConfig<CompiledAggregation>().Children.FirstOrDefault(c=>c.Name==child.Name && c.RouteUid==child.RouteUid);
                        if(oldChild!=null && oldChild.Path==child.Path && oldChild.Query.SequenceEqual(child.Query) && oldChild.ForwardHeaders.SequenceEqual(child.ForwardHeaders))
                            throw new ArgumentException("A referenced route match changed. Update its aggregate child request in the same change set.");
                    }
                }
                if(string.IsNullOrEmpty(route.ClusterId) || !snapshot.Clusters.Any(c=>c.ClusterId==route.ClusterId) ||
                    route.Match.Methods is {Count:>0} && !route.Match.Methods.Contains("GET",StringComparer.OrdinalIgnoreCase) ||
                    targetPlan.Plugins.Any(p=>new[]{"redirect","request-aggregation"}.Contains(p.Binding.PluginId,StringComparer.OrdinalIgnoreCase)))
                    throw new ArgumentException("Aggregate children must be available GET proxy routes without redirects or aggregation.");
            }
        }
    }
    public static InternalRouteCall[] Resolve(CompiledAggregation config,HttpContext parent)=>config.Children.Select(child=>
        new InternalRouteCall(child.RouteUid,Expand(child.Path,parent,true),QueryString.Create(child.Query.Select(q=>new KeyValuePair<string,string?>(q.Name,Expand(q.Value,parent,false)))).Value ?? "",child.ForwardHeaders)).ToArray();
    private static string Expand(string template,HttpContext parent,bool path)
    {
        var result=Tokens().Replace(template,match=>
        {
            string? value;
            if(match.Groups[1].Value=="route") value=parent.Request.RouteValues.TryGetValue(match.Groups[2].Value,out var route)?Convert.ToString(route,System.Globalization.CultureInfo.InvariantCulture):null;
            else {var values=parent.Request.Query[match.Groups[2].Value];value=values.Count==1?values[0]:null;}
            if(string.IsNullOrEmpty(value) || value.Length>4096 || value.Any(char.IsControl) || path && (value is "." or ".." || value.IndexOfAny(['/','\\','%'])>=0))
                throw new InternalRouteException(400,"invalid_aggregate_input");
            return path?Uri.EscapeDataString(value):value;
        });
        if(result.Length>8192) throw new InternalRouteException(400,"aggregate_input_limit");
        return result;
    }
    private static void ValidateTemplate(string template,bool path)
    {
        if(string.IsNullOrEmpty(template) || template.Length>4096 || template.Any(char.IsControl)) throw new ArgumentException("Invalid child template.");
        var literal=Tokens().Replace(template,"value");
        if(literal.Contains('{') || literal.Contains('}') || path && (!literal.StartsWith('/') || literal.StartsWith("//") || literal.IndexOfAny(['\\','?','#','%'])>=0 || literal.Split('/').Any(s=>s is "." or "..")))
            throw new ArgumentException("Use local paths and {route.name} or {query.name} placeholders.");
    }
    private static bool Name(string? name)=>!string.IsNullOrEmpty(name) && name.Length<=64 && name.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    internal static bool SafeHeader(string? header)=>header!=null && Name(header) && !header.StartsWith("X-Forwarded-",StringComparison.OrdinalIgnoreCase) &&
        !new[]{"Host","Connection","Content-Length","Transfer-Encoding","Forwarded","Accept-Encoding","Range","If-Range","If-Match","If-None-Match","If-Modified-Since","If-Unmodified-Since","Upgrade","TE","Trailer","Proxy-Authorization","Proxy-Authenticate","Keep-Alive"}.Contains(header,StringComparer.OrdinalIgnoreCase);
}
