using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>Creates and verifies bounded WebAuthn ceremonies for the configured administration account.</summary>
public interface IDashboardWebAuthnService
{
    /// <summary>Lists public credential descriptions.</summary>
    Task<DashboardWebAuthnCredentialsResponse> GetCredentialsAsync(CancellationToken cancellationToken);
    /// <summary>Begins enrollment after fresh password and configured TOTP verification.</summary>
    Task<DashboardWebAuthnCeremonyResponse> BeginEnrollmentAsync(HttpContext context, DashboardWebAuthnEnrollmentRequest request, CancellationToken cancellationToken);
    /// <summary>Verifies and stores an enrolled authenticator.</summary>
    Task<DashboardWebAuthnCredentialResponse> CompleteEnrollmentAsync(HttpContext context, DashboardWebAuthnCompletionRequest request, CancellationToken cancellationToken);
    /// <summary>Begins a browser-bound sign-in ceremony.</summary>
    Task<DashboardWebAuthnCeremonyResponse> BeginLoginAsync(HttpContext context, DashboardWebAuthnLoginRequest request, CancellationToken cancellationToken);
    /// <summary>Verifies an assertion, persists its counter, and creates a host-local JWT session.</summary>
    Task<DashboardWebAuthnLoginResponse> CompleteLoginAsync(HttpContext context, DashboardWebAuthnCompletionRequest request, CancellationToken cancellationToken);
    /// <summary>Removes a credential after fresh password and configured TOTP verification.</summary>
    Task<bool> RemoveAsync(HttpContext context, DashboardWebAuthnRemovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Fresh credential proof and a display name for authenticator enrollment.</summary>
public sealed record DashboardWebAuthnEnrollmentRequest(
    [Required, StringLength(64, MinimumLength = 1)] string Name,
    [Required, StringLength(1024)] string Password,
    [StringLength(16)] string? TwoFactorCode);
/// <summary>The configured account requesting authenticator sign-in.</summary>
public sealed record DashboardWebAuthnLoginRequest([Required, StringLength(256)] string Username);
/// <summary>An authenticator response and its single-use challenge identifier.</summary>
public sealed record DashboardWebAuthnCompletionRequest(
    [Required, StringLength(43, MinimumLength = 43)] string ChallengeId,
    JsonElement Credential, [StringLength(16)] string? TwoFactorCode);
/// <summary>Fresh credential proof authorizing removal of an authenticator.</summary>
public sealed record DashboardWebAuthnRemovalRequest(
    [Required, StringLength(1366)] string CredentialId,
    [Required, StringLength(1024)] string Password,
    [StringLength(16)] string? TwoFactorCode);
/// <summary>Browser options and the server-enforced ceremony expiry.</summary>
public sealed record DashboardWebAuthnCeremonyResponse(int Code, string ChallengeId, JsonElement Options, bool RequiresTwoFactor, DateTimeOffset ExpiresUtc);
/// <summary>A credential description without its public key or assertion data.</summary>
public sealed record DashboardWebAuthnCredentialResponse(string Id, string Name, DateTimeOffset CreatedUtc, DateTimeOffset? LastUsedUtc);
/// <summary>Configured availability and registered authenticators.</summary>
public sealed record DashboardWebAuthnCredentialsResponse(int Code, bool Enabled, IReadOnlyList<DashboardWebAuthnCredentialResponse> Credentials);
/// <summary>A verified host-local administration session.</summary>
public sealed record DashboardWebAuthnLoginResponse(int Code, string Token, DateTimeOffset ExpiresUtc);

internal sealed class DashboardWebAuthnException(int status, string message) : Exception(message)
{
    internal int Status { get; } = status;
}
