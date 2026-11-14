using System.Text.Json.Serialization;
namespace Wafish.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationUpdateStatus>))]
public enum ConfigurationUpdateStatus { Applied, ValidationRejected, VersionConflict, StorageFailed, ActivationFailed }

public readonly record struct RouteOperationResult(bool Success, string Message)
{
    public ConfigurationUpdateStatus Status { get; init; } = Success
        ? ConfigurationUpdateStatus.Applied : ConfigurationUpdateStatus.ValidationRejected;
    public long? PersistedVersion { get; init; }
    public long? ActiveVersion { get; init; }
}

public sealed class ConfigurationUpdateException(RouteOperationResult result) : Exception(result.Message)
{
    public RouteOperationResult Result { get; } = result;
}
