using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wafish.Services;

namespace Wafish.Dashboard.Infrastructure.Auth;

internal sealed class DashboardWebAuthnService : IDashboardWebAuthnService, IHostedService, IDisposable
{
    private const string BrowserCookie = "wafish_webauthn";
    private readonly DashboardOptions _options;
    private readonly DashboardSessionStore _sessions;
    private readonly AuthenticationAttemptLimiter _attempts;
    private readonly TimeProvider _clock;
    private readonly DashboardWebAuthnConfiguration? _configuration;
    private readonly DashboardWebAuthnStore? _store;
    private readonly DashboardWebAuthnChallenges _challenges;
    private readonly IFido2? _fido;

    public DashboardWebAuthnService(IOptions<DashboardOptions> options, DashboardSessionStore sessions,
        AuthenticationAttemptLimiter attempts, TimeProvider? clock = null)
    {
        _options = options.Value; _sessions = sessions; _attempts = attempts; _clock = clock ?? TimeProvider.System;
        _configuration = DashboardWebAuthnConfiguration.Create(_options);
        _challenges = new(_clock);
        if (_configuration is { } config)
        {
            _store = new(config);
            _fido = new Fido2(new Fido2Configuration { RPID = config.RelyingPartyId, RPName = config.RelyingPartyName,
                Origins = config.Origins, ChallengeSize = 32, Timeout = 120000, AllowCrossOriginRequests = false });
        }
    }
    public Task StartAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); _store?.Read(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => _store?.Dispose();

    public Task<DashboardWebAuthnCredentialsResponse> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DashboardWebAuthnCredentialsResponse(200, _configuration is not null,
            _store is null ? [] : State().Credentials.Select(Describe).ToArray()));
    }

    public Task<DashboardWebAuthnCeremonyResponse> BeginEnrollmentAsync(HttpContext context, DashboardWebAuthnEnrollmentRequest request, CancellationToken cancellationToken)
    {
        Guard(context, cancellationToken);
        var owner = SessionOwner(context);
        Proof(context, request.Password, request.TwoFactorCode);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64) throw Failure(400, "A credential name is required.");
        var state = State();
        if (state.Credentials.Length >= DashboardWebAuthnStore.Capacity) throw Failure(409, "Credential capacity reached. Remove an unused authenticator.");
        var options = _fido!.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Name = _configuration!.Account, DisplayName = _configuration.Account, Id = state.UserHandle },
            ExcludeCredentials = state.Credentials.Select(item => new PublicKeyCredentialDescriptor(WebEncoders.Base64UrlDecode(item.Id))).ToArray(),
            AuthenticatorSelection = new AuthenticatorSelection { UserVerification = UserVerificationRequirement.Required, ResidentKey = ResidentKeyRequirement.Preferred },
            AttestationPreference = AttestationConveyancePreference.None,
            PubKeyCredParams = [new(COSE.Algorithm.ES256), new(COSE.Algorithm.RS256)]
        });
        return Task.FromResult(Ceremony("enroll", owner, state, options, options.ToJson(), false, request.Name.Trim()));
    }

    public async Task<DashboardWebAuthnCredentialResponse> CompleteEnrollmentAsync(HttpContext context, DashboardWebAuthnCompletionRequest request, CancellationToken cancellationToken)
    {
        Guard(context, cancellationToken); Attempt(context);
        var challenge = Take(request.ChallengeId, "enroll", SessionOwner(context));
        var state = State();
        Current(challenge, state);
        var raw = Parse<AuthenticatorAttestationRawResponse>(request.Credential);
        if (raw.RawId is not { Length: > 0 and <= 1024 }) throw Failure(400, "Invalid authenticator response.");
        RegisteredPublicKeyCredential result;
        try
        {
            result = await _fido!.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = raw, OriginalOptions = (CredentialCreateOptions)challenge.Options,
                IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(!state.Credentials.Any(item => item.Id == WebEncoders.Base64UrlEncode(args.CredentialId)))
            }, cancellationToken);
        }
        catch (Exception error) when (VerificationFailure(error)) { throw Failure(401, "Authenticator verification failed. Start again."); }
        cancellationToken.ThrowIfCancellationRequested();
        Current(challenge, State());
        var credential = new DashboardWebAuthnStore.Credential(WebEncoders.Base64UrlEncode(result.Id), challenge.Name!, result.PublicKey,
            result.SignCount, result.IsBackupEligible, result.IsBackedUp, _clock.GetUtcNow(), null);
        Change(() => _store!.Add(challenge.CredentialGeneration, credential));
        _sessions.InvalidateAll();
        return Describe(credential);
    }

    public Task<DashboardWebAuthnCeremonyResponse> BeginLoginAsync(HttpContext context, DashboardWebAuthnLoginRequest request, CancellationToken cancellationToken)
    {
        Guard(context, cancellationToken); Attempt(context);
        if (!ControlPlaneCredentials.Equal(_configuration!.Account, request.Username)) throw Failure(401, "Authenticator sign-in is unavailable for these credentials.");
        var state = State();
        if (state.Credentials.Length == 0) throw Failure(401, "Authenticator sign-in is unavailable for these credentials.");
        var options = _fido!.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = state.Credentials.Select(item => new PublicKeyCredentialDescriptor(WebEncoders.Base64UrlDecode(item.Id))).ToArray(),
            UserVerification = UserVerificationRequirement.Required
        });
        var owner = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var result = Ceremony("login", Hash(owner), state, options, options.ToJson(), TwoFactor().Enabled);
        context.Response.Cookies.Append(BrowserCookie, owner, CookieOptions(context));
        return Task.FromResult(result);
    }

    public async Task<DashboardWebAuthnLoginResponse> CompleteLoginAsync(HttpContext context, DashboardWebAuthnCompletionRequest request, CancellationToken cancellationToken)
    {
        Guard(context, cancellationToken); Attempt(context);
        var owner = context.Request.Cookies[BrowserCookie];
        context.Response.Cookies.Delete(BrowserCookie, CookieOptions(context));
        if (owner is not { Length: 43 }) throw Failure(401, "The sign-in challenge expired. Start again.");
        var challenge = Take(request.ChallengeId, "login", Hash(owner));
        var state = State(); Current(challenge, state);
        VerifyTwoFactor(request.TwoFactorCode);
        var raw = Parse<AuthenticatorAssertionRawResponse>(request.Credential);
        if (raw.RawId is not { Length: > 0 and <= 1024 } || raw.Response is null || raw.Response.AuthenticatorData is not { Length: >= 37 })
            throw Failure(400, "Invalid authenticator response.");
        var credential = state.Credentials.SingleOrDefault(item => item.Id == WebEncoders.Base64UrlEncode(raw.RawId))
            ?? throw Failure(401, "Authenticator verification failed. Start again.");
        VerifyAssertionResult result;
        try
        {
            result = await _fido!.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = raw, OriginalOptions = (AssertionOptions)challenge.Options,
                StoredPublicKey = credential.PublicKey, StoredSignatureCounter = credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.CredentialId.AsSpan().SequenceEqual(raw.RawId)
                    && args.UserHandle.AsSpan().SequenceEqual(state.UserHandle))
            }, cancellationToken);
        }
        catch (Exception error) when (VerificationFailure(error)) { throw Failure(401, "Authenticator verification failed. Start again."); }
        if (((raw.Response.AuthenticatorData[32] & 0x08) != 0) != credential.BackupEligible)
            throw Failure(401, "Authenticator backup eligibility changed.");
        cancellationToken.ThrowIfCancellationRequested();
        Current(challenge, State());
        Change(() => _store!.RecordUse(challenge.CredentialGeneration, credential, result.SignCount, result.IsBackedUp, _clock.GetUtcNow()));
        if (!_sessions.TryCreate(_configuration!.Account, _options.Auth.JwtSecret!, challenge.SessionGeneration, out var token))
            throw Failure(503, "Session creation is unavailable. Start again.");
        return new(200, token, _clock.GetUtcNow().AddHours(8));
    }

    public Task<bool> RemoveAsync(HttpContext context, DashboardWebAuthnRemovalRequest request, CancellationToken cancellationToken)
    {
        Guard(context, cancellationToken); _ = SessionOwner(context); Proof(context, request.Password, request.TwoFactorCode);
        var removed = false;
        Change(() => removed = _store!.Remove(request.CredentialId));
        if (removed) _sessions.InvalidateAll();
        return Task.FromResult(removed);
    }

    private DashboardWebAuthnCeremonyResponse Ceremony(string operation, string? owner, DashboardWebAuthnStore.State state,
        object options, string json, bool twoFactor, string? name = null)
    {
        string id;
        try { id = _challenges.Add(operation, _configuration!.Account, owner, _sessions.Generation, state.Generation, options, name); }
        catch (InvalidOperationException) { throw Failure(503, "Authenticator challenges are unavailable. Retry later."); }
        using var document = JsonDocument.Parse(json);
        return new(200, id, document.RootElement.Clone(), twoFactor, _clock.GetUtcNow().Add(DashboardWebAuthnChallenges.Lifetime));
    }
    private DashboardWebAuthnChallenges.Challenge Take(string id, string operation, string? owner)
        => _challenges.Take(id, operation, _configuration!.Account, owner) ?? throw Failure(401, "The authenticator challenge expired or was already used. Start again.");
    private void Current(DashboardWebAuthnChallenges.Challenge challenge, DashboardWebAuthnStore.State state)
    {
        if (challenge.SessionGeneration != _sessions.Generation || challenge.CredentialGeneration != state.Generation)
            throw Failure(401, "Security settings changed. Start again.");
    }
    private void Guard(HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configuration is null) throw Failure(404, "WebAuthn is not enabled.");
        if (context.Request.Headers.Origin.Count != 1 || !_configuration.Origins.Contains(context.Request.Headers.Origin.ToString()))
            throw Failure(403, "The request origin is not allowed.");
    }
    private void Attempt(HttpContext context)
    {
        if (!_attempts.TryAcquire(context, "webauthn", out _) || !_attempts.TryAcquireAccount(context))
            throw Failure(429, "Too many authentication attempts. Retry later.");
    }
    private void Proof(HttpContext context, string password, string? code)
    {
        Attempt(context);
        if (password is not { Length: > 0 and <= 1024 } || !ControlPlaneCredentials.Equal(_options.Auth.JwtPassword, password))
            throw Failure(401, "Current credentials are required.");
        VerifyTwoFactor(code);
    }
    private TwoFactorStateStore.State TwoFactor()
    {
        try { return TwoFactorStateStore.Read(_options.Auth.TwoFactorStatePath, new(_options.Auth.EnableTwoFactor, _options.Auth.TwoFactorSecret)); }
        catch (Exception error) when (StorageFailure(error)) { throw Failure(503, "Two-factor verification is unavailable."); }
    }
    private void VerifyTwoFactor(string? code)
    {
        var factor = TwoFactor();
        try
        {
            if (factor.Enabled && (code is not { Length: 6 } || !TotpHelper.ValidateCode(factor.Secret!, code)))
                throw Failure(401, "A valid two-factor code is required.");
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        { throw Failure(503, "Two-factor verification is unavailable."); }
    }
    private string SessionOwner(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : context.Request.Cookies["dashboard_token"];
        if (string.IsNullOrWhiteSpace(token) || !_sessions.IsActive(token)) throw Failure(401, "Sign in again before managing authenticators.");
        return Hash(token);
    }
    private CookieOptions CookieOptions(HttpContext context) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, MaxAge = DashboardWebAuthnChallenges.Lifetime,
        Path = context.Request.PathBase + "/" + _options.RoutePrefix.Trim('/') + "/api/webauthn"
    };
    private DashboardWebAuthnStore.State State()
    {
        try { return _store!.Read(); }
        catch (Exception error) when (StorageFailure(error)) { throw Failure(503, "Authenticator storage is unavailable."); }
    }
    private static void Change(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { throw Failure(409, "Authenticator state changed. Start again."); }
        catch (Exception error) when (StorageFailure(error)) { throw Failure(503, "Authenticator storage is unavailable."); }
    }
    private static T Parse<T>(JsonElement element) where T : class
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object || element.GetRawText().Length > 65536) throw new JsonException();
            var value = element.Deserialize<T>() ?? throw new JsonException();
            Validator.ValidateObject(value, new ValidationContext(value), validateAllProperties: true);
            return value;
        }
        catch (Exception error) when (error is JsonException or ValidationException or FormatException or ArgumentException)
        { throw Failure(400, "Invalid authenticator response."); }
    }
    private static bool VerificationFailure(Exception error) => error is Fido2VerificationException or ArgumentException or FormatException or CryptographicException or JsonException;
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or FormatException;
    private static DashboardWebAuthnException Failure(int status, string message) => new(status, message);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static DashboardWebAuthnCredentialResponse Describe(DashboardWebAuthnStore.Credential item) => new(item.Id, item.Name, item.CreatedUtc, item.LastUsedUtc);
}
