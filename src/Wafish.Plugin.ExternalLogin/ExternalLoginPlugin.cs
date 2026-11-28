using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Plugins;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.ExternalLogin;

public sealed class ExternalLoginPlugin(ExternalLoginCatalog? catalog = null) : IGatewayPlugin
{
    public string PluginId => "external-login";
    public string DisplayName => "External login";
    public string Version => "1.0";
    public PluginManifest Manifest => new(PluginId, DisplayName, Version, [PluginScope.Route],
        [PluginCapability.RequestMiddleware], 10, new(RequestMiddleware: true),
        [new(1, "builtin://plugins/external-login/schemas/config-v1.json", """
        {"type":"object","additionalProperties":false,"required":["site","mode"],"properties":{
          "site":{"type":"string","minLength":1,"maxLength":64},
          "mode":{"type":"string","enum":["page","api"],"default":"page"},
          "apiTarget":{"type":"string","minLength":1,"maxLength":64}
        }}
        """.Replace("\"type\":\"object\"", "\"x-wafish-api-targets\":" + JsonSerializer.Serialize(catalog?.Options.Sites.ToDictionary(s => s.Id, s => s.ApiTargets.Select(t => t.Id).ToArray()) ?? new Dictionary<string, string[]>()) + ",\"type\":\"object\"")
            .Replace("\"site\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}",
            catalog == null ? "\"site\":{\"type\":\"string\"}" : "\"site\":" + JsonSerializer.Serialize(new { type = "string", @enum = catalog.Options.Sites.Select(s => s.Id) }))
            .Replace("\"apiTarget\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64}",
                catalog == null ? "\"apiTarget\":{\"type\":\"string\"}" : "\"apiTarget\":" + JsonSerializer.Serialize(new { type = "string", @enum = catalog.Options.Sites.SelectMany(s => s.ApiTargets).Select(t => t.Id).Distinct() })))], "Require a visitor session and optionally forward a server-held API token.")
        { RequiredHostCapabilities = [new("wafish.external-login")] };
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
}

public sealed record ExternalLoginBinding(string Site, string Mode, string? ApiTarget = null);

public sealed class ExternalLoginCompiler : IRoutePluginCompiler, IGatewaySnapshotValidator
{
    public const string MetadataKey = "wafish:external-login";
    private readonly ExternalLoginCatalog? _catalog;
    private readonly RoutePolicyPipelineSupport? _support;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public ExternalLoginCompiler(ExternalLoginCatalog? catalog = null, RoutePolicyPipelineSupport? support = null)
    { _catalog = catalog; _support = support; }
    public bool CanCompile(string pluginId) => pluginId.Equals("external-login", StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        _support?.RequireEnabled();
        if (_catalog == null) throw new ArgumentException("Register AddWafishExternalLogin before enabling visitor authentication.");
        if (binding.SchemaVersion != 1) throw new ArgumentException("Unsupported external login schema.");
        var config = JsonSerializer.Deserialize<ExternalLoginBinding>(binding.ConfigJson, Json) ?? throw new ArgumentException("External login configuration is required.");
        var site = _catalog.Site(config.Site);
        if (config.Mode is not ("page" or "api") || config.ApiTarget != null && (config.Mode != "api" || !site.ApiTargets.Any(t => t.Id == config.ApiTarget)))
            throw new ArgumentException("Invalid external login mode or API target.");
        if (route.AuthorizationPolicy?.Equals("anonymous", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("External login conflicts with anonymous authorization.");
        var origin = new Uri(site.Origin);
        if (route.Match.Hosts is not { Count: 1 } || !route.Match.Hosts[0].Equals(origin.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("External login routes must match the site's exact public authority.");
        if (route.Match.Path?.StartsWith("/_wafish/auth", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("Authentication paths are reserved.");
        var metadata = new Dictionary<string, string>(route.Metadata ?? new Dictionary<string, string>()) { [MetadataKey] = binding.ConfigJson };
        return new(binding, PluginExecutionStage.PreProxy, config, route with { Metadata = metadata });
    }
    public void Validate(GatewaySnapshot snapshot)
    {
        foreach (var route in snapshot.Routes.Where(r => r.Metadata?.ContainsKey(MetadataKey) == true))
        {
            var binding = Read(route)!;
            if (binding.ApiTarget == null) continue;
            var target = _catalog!.Site(binding.Site).ApiTargets.Single(t => t.Id == binding.ApiTarget);
            var cluster = snapshot.Clusters.FirstOrDefault(c => c.ClusterId == route.ClusterId);
            if (cluster == null || cluster.HttpClient?.DangerousAcceptAnyServerCertificate == true)
                throw new ArgumentException("API token forwarding requires a cluster with certificate validation.");
            foreach (var destination in cluster.Destinations?.Values ?? [])
                if (!Uri.TryCreate(destination.Address, UriKind.Absolute, out var uri) || !target.Origins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))
                    throw new ArgumentException("API destination is outside the configured token origins.");
        }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RouteConfig, ExternalLoginBinding> Bindings = new();
    internal static ExternalLoginBinding? Read(RouteConfig? route) => route?.Metadata?.ContainsKey(MetadataKey) == true
        ? Bindings.GetValue(route, r => JsonSerializer.Deserialize<ExternalLoginBinding>(r.Metadata![MetadataKey], Json)!) : null;
}
