using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Auth;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>Registered authenticators for the configured JWT administration account.</summary>
[ApiController]
[Route("api/webauthn")]
[RequestSizeLimit(65536)]
[Produces("application/json")]
[DashboardPermission(DashboardPermission.CredentialManagement)]
public sealed class DashboardWebAuthnController(IDashboardWebAuthnService service) : ControllerBase
{
    /// <summary>List enrolled authenticators without their public keys.</summary>
    [HttpGet("credentials")]
    [ProducesResponseType<DashboardWebAuthnCredentialsResponse>(200)]
    public Task<IActionResult> GetCredentials(CancellationToken cancellationToken)
        => Execute(async () => Ok(await service.GetCredentialsAsync(cancellationToken)));

    /// <summary>Read one enrolled authenticator's description.</summary>
    [HttpGet("credentials/{credentialId}")]
    [ProducesResponseType<DashboardWebAuthnCredentialResponse>(200)]
    [ProducesResponseType(404)]
    public Task<IActionResult> GetCredential(string credentialId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var credentials = await service.GetCredentialsAsync(cancellationToken);
            var item = credentials.Credentials.SingleOrDefault(item => item.Id == credentialId);
            return item is null ? NotFound(new { code = 404, message = "Authenticator not found." }) : Ok(item);
        });

    /// <summary>Begin enrollment with fresh password and configured TOTP proof.</summary>
    [HttpPost("enrollment/options")]
    [ProducesResponseType<DashboardWebAuthnCeremonyResponse>(200)]
    public Task<IActionResult> BeginEnrollment(DashboardWebAuthnEnrollmentRequest request, CancellationToken cancellationToken)
        => Execute(async () => Ok(await service.BeginEnrollmentAsync(HttpContext, request, cancellationToken)));

    /// <summary>Verify enrollment, persist the authenticator, and revoke existing sessions.</summary>
    [HttpPost("enrollment/complete")]
    [ProducesResponseType<DashboardWebAuthnCredentialResponse>(201)]
    public Task<IActionResult> CompleteEnrollment(DashboardWebAuthnCompletionRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var item = await service.CompleteEnrollmentAsync(HttpContext, request, cancellationToken);
            return CreatedAtAction(nameof(GetCredential), new { credentialId = item.Id }, item);
        });

    /// <summary>Begin a browser-bound authenticator sign-in ceremony.</summary>
    [HttpPost("login/options")]
    [ProducesResponseType<DashboardWebAuthnCeremonyResponse>(200)]
    public Task<IActionResult> BeginLogin(DashboardWebAuthnLoginRequest request, CancellationToken cancellationToken)
        => Execute(async () => Ok(await service.BeginLoginAsync(HttpContext, request, cancellationToken)));

    /// <summary>Verify the authenticator and configured TOTP, then create a revocable JWT session.</summary>
    [HttpPost("login/complete")]
    [ProducesResponseType<DashboardWebAuthnLoginResponse>(200)]
    public Task<IActionResult> CompleteLogin(DashboardWebAuthnCompletionRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var result = await service.CompleteLoginAsync(HttpContext, request, cancellationToken);
            Response.Cookies.Append("dashboard_token", result.Token, new CookieOptions
            { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Expires = result.ExpiresUtc });
            return Ok(result);
        });

    /// <summary>Remove an authenticator with fresh credential proof and revoke sessions after persistence.</summary>
    [HttpPost("credentials/remove")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public Task<IActionResult> Remove(DashboardWebAuthnRemovalRequest request, CancellationToken cancellationToken)
        => Execute(async () => await service.RemoveAsync(HttpContext, request, cancellationToken)
            ? NoContent() : NotFound(new { code = 404, message = "Authenticator not found." }));

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (DashboardWebAuthnException error) { return StatusCode(error.Status, new { code = error.Status, message = error.Message }); }
    }
}
