using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wafish.Models;

/// <summary>Shared configuration response. Failure responses use the HTTP Problem Details fields.</summary>
public sealed record ConfigurationMutationResponse
{
    /// <summary>HTTP status code, including on failures.</summary>
    public required int Status { get; init; }
    /// <summary>Compatibility alias for the HTTP status code.</summary>
    public int Code => Status;
    /// <summary>Stable operation outcome, separate from the HTTP status.</summary>
    public required string Outcome { get; init; }
    /// <summary>True when the requested operation completed successfully.</summary>
    public required bool Success { get; init; }
    /// <summary>Safe operation summary.</summary>
    public required string Message { get; init; }
    /// <summary>Confirmed stored revision, or null when this response cannot establish it.</summary>
    public long? PersistedVersion { get; init; }
    /// <summary>Confirmed active revision, or null when this response cannot establish it.</summary>
    public long? ActiveVersion { get; init; }
    /// <summary>Problem type for failed operations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type => Success ? null : "about:blank";
    /// <summary>Problem title for failed operations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title => Success ? null : Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(Status);
    /// <summary>Safe problem detail for failed operations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail => Success ? null : Message;
    /// <summary>Endpoint-specific fields retained at their existing JSON locations.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Data { get; init; } = new(StringComparer.Ordinal);
}
