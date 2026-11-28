using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wafish.Plugin.ExternalLogin;

public sealed class ExternalLoginOptions
{
    public string RedisConnectionReference { get; set; } = "";
    public string Namespace { get; set; } = "wafish-visitor";
    public string KeyRingPath { get; set; } = "";
    public string? KeyCertificatePath { get; set; }
    public string? KeyCertificatePasswordReference { get; set; }
    public ExternalLoginSite[] Sites { get; set; } = [];
}

public sealed class ExternalLoginSite
{
    public string Id { get; set; } = "";
    public string Origin { get; set; } = "";
    public int IdleMinutes { get; set; } = 30;
    public int AbsoluteMinutes { get; set; } = 480;
    public ExternalLoginProvider[] Providers { get; set; } = [];
    public ExternalLoginApiTarget[] ApiTargets { get; set; } = [];
}

public sealed class ExternalLoginProvider
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecretReference { get; set; } = "";
    public string[] Scopes { get; set; } = [];
}

public sealed class ExternalLoginApiTarget
{
    public string Id { get; set; } = "";
    public string[] Origins { get; set; } = [];
    public string Audience { get; set; } = "";
    public string[] RequiredScopes { get; set; } = [];
}

/// <summary>A private startup copy. Configuration reloads never alter authentication schemes.</summary>
public sealed class ExternalLoginCatalog
{
    internal ExternalLoginOptions Options { get; }
    private readonly Dictionary<string, string> _stamps;
    internal string Stamp(string site) => _stamps[site];
    public ExternalLoginCatalog(ExternalLoginOptions options)
    {
        Options = JsonSerializer.Deserialize<ExternalLoginOptions>(JsonSerializer.Serialize(options))!;
        // ConfigurationBinder appends to initialized arrays. Apply defaults only after binding and cloning.
        foreach (var provider in Options.Sites?.Where(s => s != null).SelectMany(s => s.Providers ?? []) ?? [])
            if (provider?.Scopes is { Length: 0 }) provider.Scopes = ["openid", "profile"];
        Validate(Options);
        _stamps = Options.Sites!.ToDictionary(s => s.Id, ProfileStamp, StringComparer.Ordinal);
    }
    internal ExternalLoginSite Site(string id) => Options.Sites.SingleOrDefault(s => s.Id == id)
        ?? throw new ArgumentException("Unknown external login site.");
    internal ExternalLoginSite? ForOrigin(string origin) => Options.Sites.SingleOrDefault(s => s.Origin == origin);
    internal static string ProfileStamp(ExternalLoginSite site) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(site)));
    internal static string CookieScheme(string site) => "Wafish.Visitor." + site;
    internal static string OidcScheme(string site, string provider) => CookieScheme(site) + "." + provider;
    internal static string Callback(string site, string provider) => $"/_wafish/auth/callback/{site}/{provider}";
    internal static bool Identifier(string value) => value is { Length: > 0 and <= 64 } && Regex.IsMatch(value, "^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);
    internal static bool HttpsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && value == uri.GetLeftPart(UriPartial.Authority);
    private static void Validate(ExternalLoginOptions options)
    {
        if (!Identifier(options.Namespace) || !Path.IsPathFullyQualified(options.KeyRingPath) || string.IsNullOrWhiteSpace(options.RedisConnectionReference))
            throw new ArgumentException("External login requires a namespace, absolute key directory, and Redis configuration reference.");
        if (options.Sites is not { Length: >= 1 and <= 32 } || options.Sites.Select(s => s.Id).Distinct().Count() != options.Sites.Length ||
            options.Sites.Select(s => s.Origin).Distinct().Count() != options.Sites.Length) throw new ArgumentException("Specify unique external login sites and origins.");
        foreach (var site in options.Sites)
        {
            if (!Identifier(site.Id) || !HttpsOrigin(site.Origin) || site.IdleMinutes is < 1 or > 480 || site.AbsoluteMinutes < site.IdleMinutes || site.AbsoluteMinutes > 1440)
                throw new ArgumentException("Invalid external login site or session lifetime.");
            if (options.Sites.Any(s => s != site && new Uri(s.Origin).Host == new Uri(site.Origin).Host))
                throw new ArgumentException("Site cookie hosts must be unique, including across ports.");
            if (site.Providers is not { Length: >= 1 and <= 16 } || site.Providers.Select(p => p.Id).Distinct().Count() != site.Providers.Length)
                throw new ArgumentException("Specify unique OIDC providers.");
            foreach (var provider in site.Providers)
            {
                if (!Identifier(provider.Id) || !Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority) || authority.Scheme != "https" ||
                    authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0 || string.IsNullOrWhiteSpace(provider.ClientId) ||
                    string.IsNullOrWhiteSpace(provider.ClientSecretReference) || provider.DisplayName.Length > 128 ||
                    provider.Scopes is not { Length: >= 1 and <= 32 } || !provider.Scopes.Contains("openid") || provider.Scopes.Any(s => !Scope(s)))
                    throw new ArgumentException("Invalid OIDC provider settings.");
            }
            if (site.ApiTargets == null || site.ApiTargets.Length > 32 || site.ApiTargets.Select(t => t.Id).Distinct().Count() != site.ApiTargets.Length)
                throw new ArgumentException("Specify unique API targets.");
            foreach (var target in site.ApiTargets)
                if (!Identifier(target.Id) || string.IsNullOrWhiteSpace(target.Audience) || target.Audience.Length > 256 ||
                    target.Origins is not { Length: >= 1 and <= 16 } || target.Origins.Any(o => !HttpsOrigin(o)) ||
                    target.RequiredScopes is not { Length: >= 1 and <= 32 } || target.RequiredScopes.Any(s => !Scope(s)))
                    throw new ArgumentException("Invalid API target settings.");
        }
    }
    internal static bool Scope(string value) => value is { Length: >= 1 and <= 256 } && value.All(c => c is >= '!' and <= '~' && c != '"' && c != '\\');
}

internal static class AuthenticationPropertyLookup
{
    internal static string? GetItem(this IDictionary<string, string?> properties, string key) => properties.TryGetValue(key, out var value) ? value : null;
}
