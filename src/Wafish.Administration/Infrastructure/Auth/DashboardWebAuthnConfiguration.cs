using Microsoft.Extensions.Options;

namespace Wafish.Dashboard.Infrastructure.Auth;

internal sealed record DashboardWebAuthnConfiguration(string Account, string RelyingPartyId, string RelyingPartyName,
    HashSet<string> Origins, string StorePath)
{
    public static DashboardWebAuthnConfiguration? Create(DashboardOptions options)
    {
        var auth = options.Auth;
        var web = auth.WebAuthn;
        if (!web.Enabled) return null;
        void Require(bool valid, string message)
        {
            if (!valid) throw new OptionsValidationException(nameof(DashboardOptions), typeof(DashboardOptions), [message]);
        }
        Require((auth.AuthMode is DashboardAuthMode.DefaultJwt or DashboardAuthMode.CustomJwt) && auth.AuthorizeRequest is null,
            "WebAuthn requires configured JWT authentication without a custom authorization delegate.");
        Require(!string.IsNullOrWhiteSpace(web.RelyingPartyId) && web.RelyingPartyId.Length <= 253
            && Uri.CheckHostName(web.RelyingPartyId) == UriHostNameType.Dns && !web.RelyingPartyId.EndsWith('.'), "WebAuthn requires a DNS relying-party ID.");
        Require(!string.IsNullOrWhiteSpace(web.RelyingPartyName) && web.RelyingPartyName.Length <= 128, "WebAuthn relying-party name must contain 1..128 characters.");
        Require(web.Origins is { Length: >= 1 and <= 16 }, "WebAuthn requires 1..16 explicit origins.");
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in web.Origins)
        {
            Require(value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out _), "Invalid WebAuthn origin.");
            var origin = new Uri(value);
            Require((origin.Scheme == "https" || origin.Scheme == "http" && origin.Host == "localhost")
                && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.AbsolutePath == "/"
                && string.Equals(origin.IdnHost, web.RelyingPartyId, StringComparison.OrdinalIgnoreCase),
                "WebAuthn origins require HTTPS, or HTTP localhost, and must match the relying-party host exactly.");
            Require(origins.Add(origin.GetLeftPart(UriPartial.Authority)), "Duplicate WebAuthn origin.");
        }
        Require(!string.IsNullOrWhiteSpace(web.CredentialStorePath), "WebAuthn requires a credential store path.");
        var path = Path.GetFullPath(web.CredentialStorePath);
        Require(!string.Equals(path, Path.GetFullPath(auth.TwoFactorStatePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            "WebAuthn and TOTP must use separate state files.");
        var account = auth.AuthMode == DashboardAuthMode.DefaultJwt ? "admin" : auth.JwtUsername;
        Require(!string.IsNullOrWhiteSpace(account) && account.Length <= 256, "WebAuthn requires a configured account of at most 256 characters.");
        return new(account!, web.RelyingPartyId.ToLowerInvariant(), web.RelyingPartyName, origins, path);
    }
}
