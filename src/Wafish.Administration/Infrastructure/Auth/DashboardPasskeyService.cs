using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>Host-local passkey ceremonies and protected, atomic credential persistence.</summary>
public sealed class DashboardPasskeyService : IDisposable
{
    internal const int CredentialCapacity = 8;
    internal const int ChallengeCapacity = 128;
    private readonly DashboardAuthOptions _auth;
    private readonly DashboardSessionStore _sessions;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Ceremony> _pending = new(StringComparer.Ordinal);
    private readonly Fido2? _fido;
    private readonly string _account;
    private readonly byte[] _userId;
    private readonly string _fingerprint;
    private Credential[] _credentials = [];
    public bool Enabled => _fido is not null;

    public DashboardPasskeyService(IOptions<DashboardOptions> options, DashboardSessionStore sessions, TimeProvider? clock = null)
    {
        _auth = options.Value.Auth; _sessions = sessions; _clock = clock ?? TimeProvider.System;
        _account = _auth.AuthMode == DashboardAuthMode.DefaultJwt ? "admin" : _auth.JwtUsername ?? "";
        _userId = SHA256.HashData(Encoding.UTF8.GetBytes(_account));
        var settings = _auth.Passkeys;
        _fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.RelyingPartyId + "\0" + settings.Origin + "\0" + _account)));
        if (!settings.Enabled) return;
        if (_auth.AuthMode is not (DashboardAuthMode.DefaultJwt or DashboardAuthMode.CustomJwt) || _auth.AuthorizeRequest is not null || _account.Length == 0)
            throw new ArgumentException("Passkeys require a configured JWT account without a custom authentication delegate.");
        if (!Uri.TryCreate(settings.Origin, UriKind.Absolute, out var origin) ||
            origin.Host != settings.RelyingPartyId || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Scheme != "https" && !(origin.Scheme == "http" && origin.Host == "localhost"))
            throw new ArgumentException("Passkeys require an explicit HTTPS origin matching the relying-party host. HTTP localhost is permitted for development.");
        if (!Path.IsPathFullyQualified(settings.StatePath)) throw new ArgumentException("Passkey state requires an absolute path.");
        _fido = new Fido2(new Fido2Configuration { RPID = settings.RelyingPartyId, RPName = "Wafish administration", Origins = new HashSet<string> { origin.GetLeftPart(UriPartial.Authority) }, ChallengeSize = 32, Timeout = 120000 });
        if (File.Exists(settings.StatePath))
        {
            if (new FileInfo(settings.StatePath).Length > 65536) throw new InvalidDataException("Passkey state exceeds its bound.");
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(settings.StatePath)) ?? throw new InvalidDataException("Invalid passkey state.");
            if (state.Fingerprint != _fingerprint || state.Credentials is null || state.Credentials.Length > CredentialCapacity ||
                state.Credentials.Any(c => c.Id is null || c.Id.Length is < 1 or > 1024 || c.PublicKey is null || c.PublicKey.Length is < 1 or > 4096 || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 64) ||
                state.Credentials.Select(c => Convert.ToBase64String(c.Id)).Distinct().Count() != state.Credentials.Length)
                throw new InvalidDataException("Passkey state does not match this account or contains invalid credentials.");
            _credentials = state.Credentials;
        }
    }

    internal async Task<PasskeyDescriptor[]> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _credentials.Select(c => new PasskeyDescriptor(Convert.ToBase64String(c.Id), c.Name, c.CreatedAt)).ToArray(); }
        finally { _gate.Release(); }
    }

    internal async Task<object> BeginAsync(string? name, string? session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Enabled) throw new InvalidOperationException("Passkeys are disabled.");
            var now = _clock.GetUtcNow();
            foreach (var expired in _pending.Where(p => p.Value.ExpiresAt <= now || p.Value.Generation != _sessions.Generation).Select(p => p.Key).ToArray()) _pending.Remove(expired);
            if (_pending.Count >= ChallengeCapacity) throw new InvalidOperationException("Passkey challenge capacity reached.");
            string json;
            if (session is not null)
            {
                if (!_sessions.IsActive(session)) throw new ArgumentException("An active JWT session is required.");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl)) throw new ArgumentException("Passkey names require 1–64 printable characters.");
                if (_credentials.Length >= CredentialCapacity) throw new InvalidOperationException("Passkey credential capacity reached.");
                json = _fido!.RequestNewCredential(new RequestNewCredentialParams
                {
                    User = new Fido2User { Id = _userId, Name = _account, DisplayName = _account },
                    ExcludeCredentials = _credentials.Select(c => new PublicKeyCredentialDescriptor(c.Id)).ToArray(),
                    AuthenticatorSelection = new AuthenticatorSelection { UserVerification = UserVerificationRequirement.Required, ResidentKey = ResidentKeyRequirement.Preferred },
                    AttestationPreference = AttestationConveyancePreference.None
                }).ToJson();
            }
            else
            {
                if (_credentials.Length == 0) throw new InvalidOperationException("No passkeys are registered. Use password recovery.");
                json = _fido!.GetAssertionOptions(new GetAssertionOptionsParams
                {
                    AllowedCredentials = _credentials.Select(c => new PublicKeyCredentialDescriptor(c.Id)).ToArray(),
                    UserVerification = UserVerificationRequirement.Required
                }).ToJson();
            }
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _pending.Add(id, new(json, name, session is null ? null : SessionKey(session), _sessions.Generation, now.AddMinutes(2)));
            using var parsed = JsonDocument.Parse(json);
            return new { id, publicKey = parsed.RootElement.Clone() };
        }
        finally { _gate.Release(); }
    }

    private Ceremony Take(string id, string? session)
    {
        if (!_pending.Remove(id, out var pending) || pending.ExpiresAt <= _clock.GetUtcNow() || pending.Generation != _sessions.Generation ||
            pending.Session != (session is null ? null : SessionKey(session)) || session is not null && !_sessions.IsActive(session))
            throw new ArgumentException("Passkey challenge is invalid or expired.");
        return pending;
    }

    internal async Task<string> RegisterAsync(string id, AuthenticatorAttestationRawResponse response, string session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = Take(id, session);
            if (response?.Response is null || response.RawId is null || response.RawId.Length is < 1 or > 1024) throw new ArgumentException("Invalid credential response.");
            if (_credentials.Length >= CredentialCapacity) throw new InvalidOperationException("Passkey credential capacity reached.");
            var verified = await _fido!.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response, OriginalOptions = CredentialCreateOptions.FromJson(pending.Options),
                IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(!_credentials.Any(c => c.Id.SequenceEqual(args.CredentialId)))
            }, cancellationToken);
            if (verified.Id.Length is < 1 or > 1024 || verified.PublicKey.Length is < 1 or > 4096)
                throw new ArgumentException("Credential material exceeds its storage bounds.");
            if (!_sessions.IsActive(session) || pending.Generation != _sessions.Generation) throw new ArgumentException("Session expired during enrollment.");
            await PersistAsync([.. _credentials, new(verified.Id, verified.PublicKey, verified.SignCount, pending.Name!, _clock.GetUtcNow())], cancellationToken, session, pending.Generation);
            return Convert.ToBase64String(verified.Id);
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> AuthenticateAsync(string id, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = Take(id, null);
            if (response?.Response is null || response.RawId is null || response.RawId.Length is < 1 or > 1024) throw new ArgumentException("Invalid credential response.");
            var credential = _credentials.SingleOrDefault(c => c.Id.SequenceEqual(response.RawId)) ?? throw new ArgumentException("Unknown passkey.");
            var verified = await _fido!.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response, OriginalOptions = AssertionOptions.FromJson(pending.Options),
                StoredPublicKey = credential.PublicKey, StoredSignatureCounter = credential.Counter,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle is not null && args.UserHandle.SequenceEqual(_userId) && args.CredentialId.SequenceEqual(credential.Id))
            }, cancellationToken);
            await PersistAsync(_credentials.Select(c => ReferenceEquals(c, credential) ? c with { Counter = verified.SignCount } : c).ToArray(), cancellationToken);
            if (!_sessions.TryCreate(_account, _auth.JwtSecret!, pending.Generation, out var token)) throw new InvalidOperationException("Session creation is unavailable.");
            return token;
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var remaining = _credentials.Where(c => Convert.ToBase64String(c.Id) != id).ToArray();
            if (remaining.Length == _credentials.Length) return false;
            await PersistAsync(remaining, cancellationToken);
            _pending.Clear(); _sessions.InvalidateAll();
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(Credential[] credentials, CancellationToken cancellationToken, string? session = null, long generation = 0)
    {
        var path = _auth.Passkeys.StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, new State(_fingerprint, credentials), cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            void Commit()
            {
                File.Move(temporary, path, overwrite: true);
                _credentials = credentials;
            }
            if (session is null) Commit();
            else if (!_sessions.TryCommit(session, generation, Commit)) throw new ArgumentException("Session expired during enrollment.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _gate.Dispose();
    private static string SessionKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private sealed record Ceremony(string Options, string? Name, string? Session, long Generation, DateTimeOffset ExpiresAt);
    private sealed record Credential(byte[] Id, byte[] PublicKey, uint Counter, string Name, DateTimeOffset CreatedAt);
    private sealed record State(string Fingerprint, Credential[] Credentials);
}

/// <summary>Public passkey information without credential key material.</summary>
public sealed record PasskeyDescriptor(string Id, string Name, DateTimeOffset CreatedAt);
