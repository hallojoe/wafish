using System.Collections.Frozen;

namespace SampleDiscoveryCatalog;

public interface IDiscoveryCatalog
{
    CatalogSelectionResponse ReadSelection();
    (string Mode, CatalogResponse Catalog) ReadCatalog();
    bool TryUpdate(CatalogSelectionRequest request);
}

/// <summary>A bounded local catalog of configured sample backends.</summary>
public sealed class DiscoveryCatalog : IDiscoveryCatalog
{
    private readonly FrozenDictionary<string, string> _addresses;
    private Selection _current;
    private sealed record Selection(string Mode, string[] Backends);

    public DiscoveryCatalog(IReadOnlyDictionary<string, string> addresses)
    {
        if (addresses.Count is < 1 or > 8) throw new ArgumentException("Configure one to eight sample backends.");
        var validated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, address) in addresses)
        {
            if (name.Length is < 1 or > 40 || !char.IsAsciiLetterLower(name[0]) ||
                name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
                throw new ArgumentException("Backend names must be bounded lowercase identifiers.");
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
                throw new ArgumentException("Sample backends must be direct loopback HTTP origins.");
            validated.Add(name, uri.AbsoluteUri);
        }
        if (validated.Values.Distinct(StringComparer.Ordinal).Count() != validated.Count)
            throw new ArgumentException("Sample backends must use distinct origins.");
        _addresses = validated.ToFrozenDictionary(StringComparer.Ordinal);
        _current = new("healthy", _addresses.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    public CatalogSelectionResponse ReadSelection()
    {
        var current = Volatile.Read(ref _current);
        return new(current.Mode, current.Backends.ToArray());
    }

    public (string Mode, CatalogResponse Catalog) ReadCatalog()
    {
        var current = Volatile.Read(ref _current);
        return (current.Mode, new(current.Backends.Select(name => _addresses[name]).ToArray()));
    }

    public bool TryUpdate(CatalogSelectionRequest request)
    {
        if (request.Mode is not ("healthy" or "unavailable" or "malformed") || request.Backends is not { Length: <= 8 } names ||
            names.Any(name => name is null || !_addresses.ContainsKey(name)) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            return false;
        Interlocked.Exchange(ref _current, new(request.Mode, names.Order(StringComparer.Ordinal).ToArray()));
        return true;
    }
}

/// <summary>Configured destination origins returned to the HTTP JSON discovery plugin.</summary>
public sealed record CatalogResponse(string[] Endpoints);
/// <summary>Selects known backend identifiers and an explicit synthetic response mode.</summary>
public sealed record CatalogSelectionRequest(string? Mode, string[]? Backends);
/// <summary>The current response mode and selected backend identifiers.</summary>
public sealed record CatalogSelectionResponse(string Mode, string[] Backends);
