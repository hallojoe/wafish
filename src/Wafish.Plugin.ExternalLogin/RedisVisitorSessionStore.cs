using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Wafish.Plugin.ExternalLogin;

/// <summary>Atomic session storage. An incomplete refresh requires a new login instead of token reuse.</summary>
internal sealed class RedisVisitorSessionStore : ITicketStore, IDisposable
{
    internal const string SessionKey = ".wafish-session";
    internal const string SiteKey = ".wafish-site";
    internal const string ProviderKey = ".wafish-provider";
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;
    private readonly IDataProtector _protector;
    private readonly ExternalLoginCatalog _catalog;
    private readonly TimeProvider _clock;
    private readonly string _prefix;
    internal RedisVisitorSessionStore(string connection, ExternalLoginCatalog catalog, IDataProtectionProvider protection, TimeProvider clock)
    {
        _catalog = catalog; _clock = clock; _prefix = catalog.Options.Namespace + ":session:";
        _protector = protection.CreateProtector("Wafish.ExternalLogin.Session.v1", catalog.Options.Namespace);
        var configuration = ConfigurationOptions.Parse(connection);
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = 5000;
        configuration.AsyncTimeout = 5000;
        configuration.ConnectRetry = 1;
        _connection = new(() => ConnectionMultiplexer.ConnectAsync(configuration));
    }
    private async Task<IDatabase> Database() => (await _connection.Value).GetDatabase();
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var site = _catalog.Site(ticket.Properties.Items[SiteKey]!);
        var expires = (_clock.GetUtcNow() + TimeSpan.FromMinutes(site.AbsoluteMinutes)).ToUnixTimeMilliseconds();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var db = await Database();
        await db.ScriptEvaluateAsync("""
            redis.call('HSET', KEYS[1], 'ticket', ARGV[1], 'absolute', ARGV[2], 'idle', ARGV[3], 'version', 0)
            redis.call('PEXPIRE', KEYS[1], ARGV[3])
            return 1
            """, [_prefix + key], [Encode(ticket), expires, site.IdleMinutes * 60000L]);
        return key;
    }
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        // The cookie handler also calls this after an explicit login with an existing cookie.
        var site = _catalog.Site(ticket.Properties.Items[SiteKey]!);
        var expires = (_clock.GetUtcNow() + TimeSpan.FromMinutes(site.AbsoluteMinutes)).ToUnixTimeMilliseconds();
        var renewed = (long)await (await Database()).ScriptEvaluateAsync("""
            if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
            redis.call('HSET', KEYS[1], 'ticket', ARGV[1], 'absolute', ARGV[2], 'idle', ARGV[3])
            redis.call('HINCRBY', KEYS[1], 'version', 1)
            redis.call('HDEL', KEYS[1], 'refresh')
            redis.call('PEXPIRE', KEYS[1], ARGV[3])
            return 1
            """, [_prefix + key], [Encode(ticket), expires, site.IdleMinutes * 60000L]);
        if (renewed != 1) throw new InvalidOperationException("Visitor session expired during login. Start a new login.");
    }
    public async Task<AuthenticationTicket?> RetrieveAsync(string key) => (await ReadAsync(key))?.Ticket;
    internal async Task<VisitorSession?> ReadAsync(string key)
    {
        if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) return null;
        var db = await Database();
        var raw = await db.ScriptEvaluateAsync("""
            local a = redis.call('HGET', KEYS[1], 'absolute')
            if not a then return nil end
            local time = redis.call('TIME')
            local now = time[1] * 1000 + math.floor(time[2] / 1000)
            local remaining = tonumber(a) - now
            if remaining <= 0 then redis.call('DEL', KEYS[1]); return nil end
            redis.call('PEXPIRE', KEYS[1], math.min(remaining, tonumber(redis.call('HGET', KEYS[1], 'idle'))))
            return {redis.call('HGET', KEYS[1], 'ticket'), redis.call('HGET', KEYS[1], 'version'), redis.call('HGET', KEYS[1], 'refresh') or ''}
            """, [_prefix + key]);
        if (raw.IsNull) return null;
        var result = (RedisResult[])raw!;
        AuthenticationTicket? ticket;
        try { ticket = TicketSerializer.Default.Deserialize(_protector.Unprotect((byte[])result[0]!)); }
        catch (CryptographicException) { await RemoveAsync(key); return null; }
        if (ticket == null) { await RemoveAsync(key); return null; }
        ticket.Properties.Items[SessionKey] = key;
        return new(key, ticket, (long)result[1], (string?)result[2] ?? "");
    }
    public async Task RemoveAsync(string key) => await (await Database()).KeyDeleteAsync(_prefix + key);
    internal async Task<bool> BeginRefreshAsync(VisitorSession session, string owner)
    {
        return (long)await (await Database()).ScriptEvaluateAsync("""
            if redis.call('HGET', KEYS[1], 'version') ~= ARGV[1] or redis.call('HEXISTS', KEYS[1], 'refresh') == 1 then return 0 end
            redis.call('HSET', KEYS[1], 'refresh', ARGV[2])
            return 1
            """, [_prefix + session.Key], [session.Version.ToString(CultureInfo.InvariantCulture), owner]) == 1;
    }
    internal async Task<bool> CompleteRefreshAsync(VisitorSession session, string owner)
    {
        return (long)await (await Database()).ScriptEvaluateAsync("""
            if redis.call('HGET', KEYS[1], 'version') ~= ARGV[1] or redis.call('HGET', KEYS[1], 'refresh') ~= ARGV[2] then return 0 end
            redis.call('HSET', KEYS[1], 'ticket', ARGV[3])
            redis.call('HINCRBY', KEYS[1], 'version', 1)
            redis.call('HDEL', KEYS[1], 'refresh')
            return 1
            """, [_prefix + session.Key], [session.Version.ToString(CultureInfo.InvariantCulture), owner, Encode(session.Ticket)]) == 1;
    }
    private byte[] Encode(AuthenticationTicket ticket)
    {
        var bytes = TicketSerializer.Default.Serialize(ticket);
        if (bytes.Length > 65536) throw new InvalidOperationException("Visitor session exceeds the storage limit.");
        return _protector.Protect(bytes);
    }
    public void Dispose()
    {
        if (_connection.IsValueCreated)
            _ = _connection.Value.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
    }
}

internal sealed record VisitorSession(string Key, AuthenticationTicket Ticket, long Version, string RefreshOwner);
