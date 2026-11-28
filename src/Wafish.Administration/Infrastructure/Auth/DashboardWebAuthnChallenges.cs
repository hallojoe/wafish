using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Wafish.Dashboard.Infrastructure.Auth;

internal sealed class DashboardWebAuthnChallenges(TimeProvider clock)
{
    internal const int Capacity = 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Challenge> _items = new(StringComparer.Ordinal);
    internal sealed record Challenge(string Operation, string Account, string? Owner, long SessionGeneration,
        long CredentialGeneration, object Options, string? Name, long Started);

    internal string Add(string operation, string account, string? owner, long sessionGeneration,
        long credentialGeneration, object options, string? name = null)
    {
        lock (_gate)
        {
            foreach (var expired in _items.Where(item => clock.GetElapsedTime(item.Value.Started) >= Lifetime).Select(item => item.Key).ToArray())
                _items.Remove(expired);
            if (_items.Count >= Capacity) throw new InvalidOperationException("WebAuthn challenge capacity reached. Retry later.");
            var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            _items.Add(id, new(operation, account, owner, sessionGeneration, credentialGeneration, options, name, clock.GetTimestamp()));
            return id;
        }
    }

    internal Challenge? Take(string? id, string operation, string account, string? owner)
    {
        lock (_gate)
        {
            if (id is not { Length: 43 } || !_items.Remove(id, out var challenge)) return null;
            return clock.GetElapsedTime(challenge.Started) < Lifetime && challenge.Operation == operation
                && challenge.Account == account && challenge.Owner == owner ? challenge : null;
        }
    }
}
