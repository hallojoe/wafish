using Fido2NetLib;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wafish.Dashboard.Infrastructure.Auth;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>Optional WebAuthn enrollment, login, and revocation for JWT administration.</summary>
[ApiController]
[Route("api/passkeys")]
[RequestSizeLimit(32768)]
[DashboardPermission(DashboardPermission.CredentialManagement)]
public sealed class DashboardPasskeysController(DashboardPasskeyService passkeys, AuthenticationAttemptLimiter attempts) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(CancellationToken cancellationToken) => Execute(async () => Ok(await passkeys.ListAsync(cancellationToken)));

    /// <summary>Returns one registered passkey without public-key material.</summary>
    [HttpGet("credential")]
    public Task<IActionResult> GetById([FromQuery] string id, CancellationToken cancellationToken) => Execute(async () =>
    {
        var credential = (await passkeys.ListAsync(cancellationToken)).SingleOrDefault(c => c.Id == id);
        return credential is null ? NotFound() : Ok(credential);
    });

    [HttpPost("registration/options")]
    public Task<IActionResult> RegistrationOptions(NameRequest request, CancellationToken cancellationToken) => Execute(async () =>
        Ok(await passkeys.BeginAsync(request.Name, Session(), cancellationToken)));

    [HttpPost("registration")]
    public Task<IActionResult> Register(RegistrationRequest request, CancellationToken cancellationToken) => Execute(async () =>
    {
        var id = await passkeys.RegisterAsync(request.Id, request.Credential, Session(), cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id }, new { code = 201, id });
    });

    [HttpPost("login/options")]
    public Task<IActionResult> LoginOptions(CancellationToken cancellationToken) => Execute(async () =>
    {
        if (!attempts.TryAcquire(HttpContext, "passkey", out _) || !attempts.TryAcquireAccount(HttpContext)) return Limited();
        return Ok(await passkeys.BeginAsync(null, null, cancellationToken));
    });

    [HttpPost("login")]
    public Task<IActionResult> Login(AssertionRequest request, CancellationToken cancellationToken) => Execute(async () =>
    {
        if (!attempts.TryAcquire(HttpContext, "passkey", out _)) return Limited();
        var token = await passkeys.AuthenticateAsync(request.Id, request.Credential, cancellationToken);
        Response.Cookies.Append("dashboard_token", token, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddHours(8)
        });
        return Ok(new { code = 200, token });
    });

    [HttpPost("revoke")]
    public Task<IActionResult> Revoke(IdRequest request, CancellationToken cancellationToken) => Execute(async () =>
        await passkeys.RevokeAsync(request.Id, cancellationToken) ? Ok(new { code = 200 }) : NotFound());

    private string Session()
    {
        var bearer = Request.Headers.Authorization.ToString();
        return bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer[7..]
            : Request.Cookies["dashboard_token"] ?? throw new ArgumentException("An active JWT session is required.");
    }
    private IActionResult Limited() => StatusCode(429, new { code = 429, message = "Too many authentication attempts. Retry later." });
    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        if (!passkeys.Enabled) return NotFound();
        Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (Fido2VerificationException) { return BadRequest(new { code = 400, message = "Passkey verification failed." }); }
        catch (ArgumentException) { return BadRequest(new { code = 400, message = "Passkey request is invalid or expired." }); }
        catch (InvalidOperationException) { return Conflict(new { code = 409, message = "Passkey operation is unavailable. Use password sign-in or retry later." }); }
    }
    /// <summary>Human-readable authenticator name.</summary>
    public sealed record NameRequest(string Name);
    /// <summary>Registered credential identifier.</summary>
    public sealed record IdRequest(string Id);
    /// <summary>Single-use enrollment challenge and authenticator response.</summary>
    public sealed record RegistrationRequest(string Id, AuthenticatorAttestationRawResponse Credential);
    /// <summary>Single-use login challenge and authenticator assertion.</summary>
    public sealed record AssertionRequest(string Id, AuthenticatorAssertionRawResponse Credential);
}
