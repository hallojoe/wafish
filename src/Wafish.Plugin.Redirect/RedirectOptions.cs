using System.Text.Json.Serialization;

namespace Wafish.Plugin.Redirect;

[JsonConverter(typeof(JsonStringEnumConverter<RedirectMode>))]
public enum RedirectMode { Target, HttpsOnly }

[JsonConverter(typeof(JsonStringEnumConverter<RedirectQueryMode>))]
public enum RedirectQueryMode { Discard, Preserve }

public sealed record RedirectOptions
{
    public RedirectMode Mode { get; init; } = RedirectMode.Target;
    public string? Target { get; init; }
    public int StatusCode { get; init; } = 307;
    public RedirectQueryMode QueryMode { get; init; } = RedirectQueryMode.Discard;
    public string[] AllowedHosts { get; init; } = [];
    public int HttpsPort { get; init; } = 443;
}

/// <summary>One redirect evaluation. Invalid results never contain a location.</summary>
public sealed record RedirectResult(string Outcome, int? StatusCode = null, string? Location = null, string? Error = null);
