using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>Owns one bounded credential file and its exclusive process lease.</summary>
internal sealed class DashboardWebAuthnStore(DashboardWebAuthnConfiguration configuration) : IDisposable
{
    internal const int Capacity = 20;
    private const int MaximumBytes = 1024 * 1024;
    private readonly object _gate = new();
    private FileStream? _lease;
    private State? _state;
    private bool _disposed;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal sealed record Credential(
        [property: JsonRequired] string Id, [property: JsonRequired] string Name,
        [property: JsonRequired] byte[] PublicKey, [property: JsonRequired] uint SignCount,
        [property: JsonRequired] bool BackupEligible, [property: JsonRequired] bool BackedUp,
        [property: JsonRequired] DateTimeOffset CreatedUtc, [property: JsonRequired] DateTimeOffset? LastUsedUtc);
    internal sealed record State(
        [property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Account,
        [property: JsonRequired] string RelyingPartyId, [property: JsonRequired] byte[] UserHandle,
        [property: JsonRequired] long Generation, [property: JsonRequired] Credential[] Credentials);

    internal State Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not null) return _state;
            var path = configuration.StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = PrivateOptions(FileMode.OpenOrCreate);
            _lease ??= new FileStream(path + ".lock", options);
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (FileNotFoundException)
            {
                var initial = new State(1, configuration.Account, configuration.RelyingPartyId, RandomNumberGenerator.GetBytes(32), 0, []);
                Persist(initial);
                return _state!;
            }
            using (stream)
            {
                if (stream.Length > MaximumBytes) throw new InvalidDataException("WebAuthn credential file exceeds its size bound.");
                var state = JsonSerializer.Deserialize<State>(stream, Json) ?? throw new InvalidDataException("Missing WebAuthn credential state.");
                Validate(state);
                _state = state;
                return state;
            }
        }
    }

    internal void Add(long generation, Credential credential)
    {
        lock (_gate)
        {
            var state = Read();
            if (state.Generation != generation) throw new InvalidOperationException("WebAuthn credentials changed. Start again.");
            if (state.Credentials.Length >= Capacity || state.Credentials.Any(item => item.Id == credential.Id))
                throw new InvalidOperationException("Credential capacity reached or credential already registered.");
            Persist(state with { Generation = checked(state.Generation + 1), Credentials = [.. state.Credentials, credential] });
        }
    }

    internal bool Remove(string id)
    {
        lock (_gate)
        {
            var state = Read();
            if (!state.Credentials.Any(item => item.Id == id)) return false;
            Persist(state with { Generation = checked(state.Generation + 1), Credentials = state.Credentials.Where(item => item.Id != id).ToArray() });
            return true;
        }
    }

    internal void RecordUse(long generation, Credential expected, uint counter, bool backedUp, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = Read();
            if (state.Generation != generation || !state.Credentials.Any(item => ReferenceEquals(item, expected)))
                throw new InvalidOperationException("Credential changed during authentication. Start again.");
            if ((expected.SignCount != 0 || counter != 0) && counter <= expected.SignCount)
                throw new InvalidOperationException("Credential signature counter did not advance.");
            Persist(state with { Credentials = state.Credentials.Select(item => ReferenceEquals(item, expected)
                ? item with { SignCount = counter, BackedUp = backedUp, LastUsedUtc = now } : item).ToArray() });
        }
    }

    private void Validate(State state)
    {
        if (state.SchemaVersion != 1 || state.Account != configuration.Account || state.RelyingPartyId != configuration.RelyingPartyId
            || state.UserHandle is not { Length: 32 } || state.Generation < 0 || state.Credentials is null || state.Credentials.Length > Capacity)
            throw new InvalidDataException("Invalid WebAuthn credential state or account binding.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credential in state.Credentials)
        {
            if (credential is null || string.IsNullOrWhiteSpace(credential.Name) || credential.Name.Length > 64
                || credential.PublicKey is not { Length: > 0 and <= 4096 } || string.IsNullOrWhiteSpace(credential.Id)
                || credential.Id.Length > 1366 || !ids.Add(credential.Id) || credential.BackedUp && !credential.BackupEligible)
                throw new InvalidDataException("Invalid WebAuthn credential.");
            var id = WebEncoders.Base64UrlDecode(credential.Id);
            if (id.Length is < 1 or > 1024 || WebEncoders.Base64UrlEncode(id) != credential.Id)
                throw new InvalidDataException("Invalid WebAuthn credential identifier.");
        }
    }

    private void Persist(State next)
    {
        Validate(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("WebAuthn credential file exceeds its size bound.");
        var temporary = configuration.StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, PrivateOptions(FileMode.CreateNew)))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, configuration.StorePath, overwrite: true);
            _state = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static FileStreamOptions PrivateOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _lease?.Dispose(); _lease = null; }
    }
}
