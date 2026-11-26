namespace Wafish.Dashboard.Infrastructure;

/// <summary>Optional passkeys for the configured shared JWT administration account.</summary>
public sealed class DashboardPasskeyOptions
{
    public bool Enabled { get; set; }
    public string RelyingPartyId { get; set; } = "";
    public string Origin { get; set; } = "";
    public string StatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "passkeys.json");
}
