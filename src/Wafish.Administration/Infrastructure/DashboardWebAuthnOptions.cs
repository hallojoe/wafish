namespace Wafish.Dashboard.Infrastructure;

/// <summary>Optional WebAuthn settings for the configured JWT administration account.</summary>
public sealed class DashboardWebAuthnOptions
{
    /// <summary>Enable registered authenticators alongside password-based JWT login.</summary>
    public bool Enabled { get; set; }
    /// <summary>DNS host used as the relying-party identifier. It must match every allowed origin host.</summary>
    public string RelyingPartyId { get; set; } = string.Empty;
    /// <summary>Authenticator display name for this administration service.</summary>
    public string RelyingPartyName { get; set; } = "Wafish administration";
    /// <summary>Exact HTTPS origins. HTTP is permitted only for localhost development.</summary>
    public string[] Origins { get; set; } = [];
    /// <summary>Private, host-owned credential file. Do not share it between running gateway processes.</summary>
    public string CredentialStorePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "webauthn-credentials.json");
}
