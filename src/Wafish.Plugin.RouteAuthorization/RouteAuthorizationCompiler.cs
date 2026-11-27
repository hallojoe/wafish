using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.RouteAuthorization;

public sealed record ClaimRequirement(string Type, string[] AllowedValues);
public sealed class RouteAuthorizationOptions
{
    public string[] AllowedScopes { get; set; } = [];
    public ClaimRequirement[] Claims { get; set; } = [];
}
public sealed record CompiledClaimRequirement(string Type, ImmutableHashSet<string> AllowedValues);
public sealed record CompiledRouteAuthorization(ImmutableHashSet<string> AllowedScopes, ImmutableArray<CompiledClaimRequirement> Claims)
{
    public bool Allows(ClaimsPrincipal principal)
    {
        var identities = principal.Identities.Where(i => i.IsAuthenticated).ToArray();
        if (identities.Length == 0) return false;
        var claims = identities.SelectMany(i => i.Claims).ToArray();
        if (AllowedScopes.Count > 0 && !claims.Where(c => c.Type is "scope" or "scp")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Any(AllowedScopes.Contains)) return false;
        return Claims.All(requirement => claims.Any(c => c.Type == requirement.Type && requirement.AllowedValues.Contains(c.Value)));
    }
}
public sealed class RouteAuthorizationCompiler : IRoutePluginCompiler
{
    internal const string MetadataKey = "wafish:route-authorization";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly RoutePolicyPipelineSupport? _support;
    public RouteAuthorizationCompiler(RoutePolicyPipelineSupport? support = null) => _support = support;
    public bool CanCompile(string pluginId) => pluginId.Equals("route-authorization", StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        _support?.RequireEnabled();
        if (binding.SchemaVersion != 1) throw new ArgumentException("Unsupported route authorization schema.");
        if (route.AuthorizationPolicy?.Equals("anonymous", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("Route authorization cannot be bound to an anonymous route.");
        var options = JsonSerializer.Deserialize<RouteAuthorizationOptions>(binding.ConfigJson, Json) ?? throw new ArgumentException("Authorization configuration is required.");
        var compiled = Compile(options);
        var metadata = new Dictionary<string,string>(route.Metadata ?? new Dictionary<string,string>()) { [MetadataKey] = binding.ConfigJson };
        return new(binding, PluginExecutionStage.PreProxy, compiled, route with { Metadata = metadata });
    }
    public static CompiledRouteAuthorization Compile(RouteAuthorizationOptions options)
    {
        if (options.AllowedScopes == null || options.Claims == null || options.AllowedScopes.Length > 32 || options.Claims.Length > 32 || options.AllowedScopes.Length + options.Claims.Length == 0)
            throw new ArgumentException("Specify 1–32 scopes or claim requirements per category.");
        if (options.AllowedScopes.Any(s => !Valid(s,256) || s.Any(char.IsWhiteSpace)) || options.AllowedScopes.Distinct(StringComparer.Ordinal).Count() != options.AllowedScopes.Length)
            throw new ArgumentException("Invalid or duplicate allowed scopes.");
        var claims = ImmutableArray.CreateBuilder<CompiledClaimRequirement>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in options.Claims)
        {
            if (requirement == null || !Valid(requirement.Type,256) || !types.Add(requirement.Type) || requirement.AllowedValues is not {Length: >= 1 and <= 32} ||
                requirement.AllowedValues.Any(v => !Valid(v,1024)) || requirement.AllowedValues.Distinct(StringComparer.Ordinal).Count() != requirement.AllowedValues.Length)
                throw new ArgumentException("Invalid or duplicate claim requirement.");
            claims.Add(new(requirement.Type, requirement.AllowedValues.ToImmutableHashSet(StringComparer.Ordinal)));
        }
        return new(options.AllowedScopes.ToImmutableHashSet(StringComparer.Ordinal), claims.ToImmutable());
    }
    private static bool Valid(string value,int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl);
}
