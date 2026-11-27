using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.ClaimsTransform;

[JsonConverter(typeof(JsonStringEnumConverter<ClaimTarget>))]
public enum ClaimTarget { Header, Query, Path }
public sealed record ClaimMapping(string Claim, ClaimTarget Target, string Name, bool Required = true);
public sealed class ClaimsTransformOptions
{
    public string? PathTemplate { get; set; }
    public ClaimMapping[] Mappings { get; set; } = [];
}
public sealed record CompiledClaimsTransform(string? PathTemplate, ImmutableArray<ClaimMapping> Mappings);

public sealed partial class ClaimsTransformCompiler : IRoutePluginCompiler
{
    internal const string MetadataKey = "wafish:claims-transform";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly RoutePolicyPipelineSupport? _support;
    public ClaimsTransformCompiler() { }
    public ClaimsTransformCompiler(RoutePolicyPipelineSupport support) => _support = support;
    public bool CanCompile(string pluginId) => pluginId.Equals("claims-transform", StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        _support?.RequireEnabled();
        if (binding.SchemaVersion != 1) throw new ArgumentException("Unsupported claims transform schema.");
        var options = JsonSerializer.Deserialize<ClaimsTransformOptions>(binding.ConfigJson, Json) ?? throw new ArgumentException("Claims configuration is required.");
        var compiled = Compile(options);
        foreach (var transform in route.Transforms ?? [])
        {
            if (compiled.PathTemplate != null && transform.Keys.Any(k => k.StartsWith("Path", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Claims path mappings cannot overlap native path transforms.");
            foreach (var mapping in compiled.Mappings)
                if (transform.TryGetValue(mapping.Target == ClaimTarget.Header ? "RequestHeader" : "QueryValueParameter", out var target) && target.Equals(mapping.Name, StringComparison.OrdinalIgnoreCase) ||
                    mapping.Target == ClaimTarget.Query && transform.TryGetValue("QueryRouteParameter", out target) && target.Equals(mapping.Name, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Claims mapping overlaps a native transform.");
        }
        if (route.AuthorizationPolicy?.Equals("anonymous", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("Claims mapping requires authentication; anonymous routes are not supported.");
        var metadata = new Dictionary<string, string>(route.Metadata ?? new Dictionary<string, string>()) { [MetadataKey] = binding.ConfigJson };
        return new(binding, PluginExecutionStage.PreProxy, compiled, route with { Metadata = metadata });
    }

    public static CompiledClaimsTransform Compile(ClaimsTransformOptions options)
    {
        if (options.Mappings is not { Length: >= 1 and <= 32 }) throw new ArgumentException("Specify 1–32 claim mappings.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in options.Mappings)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Claim) || m.Claim.Length > 256 || m.Claim.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 256 || !NamePattern().IsMatch(m.Name) || !Enum.IsDefined(m.Target))
                throw new ArgumentException("Invalid claim mapping.");
            if (!seen.Add($"{m.Target}:{m.Name}")) throw new ArgumentException("Duplicate claim target.");
            if (m.Target == ClaimTarget.Header && ForbiddenHeader(m.Name)) throw new ArgumentException("Protected request headers cannot be mapped.");
            if (m.Target == ClaimTarget.Path && !m.Required) throw new ArgumentException("Path claims must be required.");
        }
        var pathMappings = options.Mappings.Where(m => m.Target == ClaimTarget.Path).ToArray();
        if (options.PathTemplate is { } path)
        {
            if (path.Length > 8192 || !path.StartsWith('/') || path.StartsWith("//") || path.IndexOfAny(['\\', '?', '#', '%']) >= 0 || path.Any(char.IsControl) || path.Split('/').Any(s => s is "." or ".."))
                throw new ArgumentException("Invalid downstream path template.");
            var tokens = TokenPattern().Matches(path).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var stripped = TokenPattern().Replace(path, "");
            if (stripped.Contains('{') || stripped.Contains('}') || tokens.Count == 0 || !tokens.SetEquals(pathMappings.Select(m => m.Name)))
                throw new ArgumentException("Path placeholders must match path mappings.");
        }
        else if (pathMappings.Length > 0) throw new ArgumentException("Path mappings require a path template.");
        return new(options.PathTemplate, options.Mappings.ToImmutableArray());
    }
    private static bool ForbiddenHeader(string name) => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
        new[] { "Host", "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "Forwarded", "Connection", "Keep-Alive", "Proxy-Connection", "Content-Length", "Transfer-Encoding", "TE", "Trailer", "Upgrade" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex NamePattern();
    [GeneratedRegex("\\{([A-Za-z0-9_-]+)\\}")] private static partial Regex TokenPattern();
}
