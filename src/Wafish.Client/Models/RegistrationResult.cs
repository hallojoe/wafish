using System.Text.Json.Serialization;

namespace Wafish.Models;

/// <summary>Registration outcomes shared by HTTP and gRPC client operations.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistrationStatus>))]
public enum RegistrationStatus
{
    Applied, ValidationRejected, Unauthenticated, PermissionDenied, NotFound, VersionConflict,
    StorageFailed, ActivationFailed, Disabled, Unavailable, DeadlineExceeded, InternalError, Unknown
}

/// <summary>Required caller action before repeating an operation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistrationRetryAdvice>))]
public enum RegistrationRetryAdvice { None, CorrectRequest, RefreshRevision, Retry, InspectBeforeRetry }

/// <summary>Operation result with confirmed revisions and explicit uncertainty after transport failures.</summary>
public sealed record RegistrationResult(
    bool Success,
    RegistrationStatus Status,
    string Message,
    long? PersistedVersion = null,
    long? ActiveVersion = null,
    string? RemoteStatus = null)
{
    /// <summary>True when the request succeeded, was persisted, or has an uncertain remote outcome.</summary>
    public bool MayHaveApplied => Success || Status is RegistrationStatus.ActivationFailed or RegistrationStatus.Unavailable
        or RegistrationStatus.DeadlineExceeded or RegistrationStatus.InternalError or RegistrationStatus.Unknown;

    /// <summary>Guidance only. The client does not automatically replay mutation calls.</summary>
    public RegistrationRetryAdvice RetryAdvice => Status switch
    {
        RegistrationStatus.Applied or RegistrationStatus.Disabled => RegistrationRetryAdvice.None,
        RegistrationStatus.VersionConflict => RegistrationRetryAdvice.RefreshRevision,
        RegistrationStatus.StorageFailed => RegistrationRetryAdvice.Retry,
        RegistrationStatus.ValidationRejected or RegistrationStatus.Unauthenticated or RegistrationStatus.PermissionDenied
            or RegistrationStatus.NotFound => RegistrationRetryAdvice.CorrectRequest,
        _ => RegistrationRetryAdvice.InspectBeforeRetry
    };

    internal static RegistrationResult Remote(bool success, string message, string? status, long? persisted = null, long? active = null)
    {
        var parsed = string.IsNullOrEmpty(status) && success ? RegistrationStatus.Applied
            : Enum.TryParse<RegistrationStatus>(status, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : RegistrationStatus.Unknown;
        if (success && parsed != RegistrationStatus.Applied || !success && parsed == RegistrationStatus.Applied)
            return new(false, RegistrationStatus.Unknown, "Gateway returned an inconsistent operation outcome.", persisted, active, status);
        return new(success, parsed, message, persisted, active, status);
    }
}
