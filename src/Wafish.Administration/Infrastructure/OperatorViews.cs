using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure.Auth;

namespace Wafish.Dashboard.Infrastructure;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorViewFilters(bool Historical = false, string Search = "", string Route = "", int? MinimumStatus = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorViewRequest(string Name, string Workspace, OperatorViewFilters Filters, string[] Columns);
public sealed record OperatorView(Guid Id, string Name, string Workspace, OperatorViewFilters Filters, string[] Columns, DateTimeOffset UpdatedAt);

/// <summary>Bounded protected preferences, keyed only by server-authenticated account identity.</summary>
public sealed class OperatorViewStore(IOptions<DashboardOptions> options, IDataProtectionProvider protection, TimeProvider clock)
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDataProtector _protector = protection.CreateProtector("Wafish.OperatorViews.v1");
    private readonly string _path = Path.GetFullPath(options.Value.OperatorViewsStatePath ?? options.Value.Auth.TwoFactorStatePath + ".views");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? Owner(HttpContext context)
    {
        var auth = options.Value.Auth;
        string[] identity;
        if (auth.AuthorizeRequest is not null)
        {
            var identities = context.User.Identities.Where(item => item.IsAuthenticated).ToArray();
            if (identities.Length != 1) return null;
            var account = identities[0];
            var subject = account.FindFirst(ClaimTypes.NameIdentifier) ?? account.FindFirst(account.NameClaimType);
            if (subject is null || string.IsNullOrWhiteSpace(subject.Value) || subject.Value.Length > 256) return null;
            identity = ["custom", account.AuthenticationType ?? "", subject.Issuer, subject.Value];
        }
        else
        {
            if (auth.AuthMode == DashboardAuthMode.None || context.Items[DashboardPermissionService.ActorKey] is not string actor ||
                string.IsNullOrWhiteSpace(actor) || actor.Length > 256) return null;
            identity = [auth.AuthMode.ToString(), actor];
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
    }

    public static bool Valid(OperatorViewRequest? view)
    {
        if (view is null || string.IsNullOrWhiteSpace(view.Name) || view.Name.Length > 64 || view.Name.Any(char.IsControl) ||
            view.Workspace is not ("logs" or "audit") || view.Filters is null || view.Filters.Search is null || view.Filters.Route is null ||
            view.Filters.Search.Length > 200 || view.Filters.Route.Length > 128 || view.Filters.MinimumStatus is < 100 or > 599 ||
            view.Columns is null || view.Columns.Length is < 1 or > 6 || view.Columns.Distinct(StringComparer.Ordinal).Count() != view.Columns.Length) return false;
        string[] allowed = view.Workspace == "audit" ? ["timestamp", "action", "message", "operatorName"] : ["timestamp", "method", "path", "routeId", "statusCode", "elapsedMs"];
        return view.Columns.All(column => allowed.Contains(column, StringComparer.Ordinal)) &&
            (view.Workspace != "audit" || !view.Filters.Historical && view.Filters.Route.Length == 0 && view.Filters.MinimumStatus is null);
    }

    public async Task<OperatorView[]> ListAsync(string owner, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return Read().GetValueOrDefault(owner, []).ToArray(); }
        finally { _gate.Release(); }
    }

    // Returns null for a missing owned view. Limits and duplicate names return a typed conflict.
    public async Task<OperatorView?> SaveAsync(string owner, Guid? id, OperatorViewRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) throw new ArgumentException("Invalid operator view.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Read(); var views = state.GetValueOrDefault(owner, []);
            var index = id is null ? -1 : views.FindIndex(view => view.Id == id);
            if (id is not null && index < 0) return null;
            if (id is null && (views.Count >= 20 || !state.ContainsKey(owner) && state.Count >= 128)) throw new OperatorViewConflictException("Saved view capacity reached.");
            var name = request.Name.Trim();
            if (views.Any(view => view.Id != id && view.Workspace == request.Workspace && view.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new OperatorViewConflictException("A view with this name already exists.");
            var result = new OperatorView(id ?? Guid.NewGuid(), name, request.Workspace, request.Filters, request.Columns.ToArray(), clock.GetUtcNow());
            if (index < 0) views.Add(result); else views[index] = result;
            state[owner] = views;
            Write(state, cancellationToken); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string owner, Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Read();
            if (!state.TryGetValue(owner, out var views) || views.RemoveAll(view => view.Id == id) == 0) return false;
            if (views.Count == 0) state.Remove(owner);
            Write(state, cancellationToken); return true;
        }
        finally { _gate.Release(); }
    }

    private Dictionary<string, List<OperatorView>> Read()
    {
        if (Directory.Exists(_path)) throw new IOException("Invalid saved view state path.");
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxBytes) throw new IOException("Saved view state exceeds its limit.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new IOException("Saved view state exceeds its limit.");
        var state = JsonSerializer.Deserialize<Dictionary<string, List<OperatorView>>>(_protector.Unprotect(bytes), Json) ?? throw new IOException("Invalid saved view state.");
        if (state.Count > 128 || state.Any(pair => pair.Key.Length != 64 || pair.Key.Any(c => !char.IsAsciiHexDigit(c)) ||
            pair.Value is null || pair.Value.Count > 20 ||
            pair.Value.Any(view => view is null || view.Id == Guid.Empty || !Valid(new(view.Name, view.Workspace, view.Filters, view.Columns))) ||
            pair.Value.Select(view => view.Id).Distinct().Count() != pair.Value.Count))
            throw new IOException("Invalid saved view state.");
        return state;
    }
    private void Write(Dictionary<string, List<OperatorView>> state, CancellationToken cancellationToken)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(state, Json));
        if (bytes.Length > MaxBytes) throw new IOException("Saved view state exceeds its limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, settings)) { output.Write(bytes); output.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class OperatorViewConflictException(string message) : Exception(message);
