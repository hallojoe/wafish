using Microsoft.Extensions.Configuration;

namespace Wafish.AppHost;

internal sealed record TopologyState(string Id, string DataDirectory, bool Legacy)
{
    public static TopologyState Read(IConfiguration configuration, string appHostDirectory)
    {
        var id = configuration["TopologyId"] ?? "default";
        if (id.Length is < 1 or > 40 || !char.IsAsciiLetterLower(id[0]) ||
            id.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-'))
            throw new ArgumentException("TopologyId must start with a lowercase letter and contain 1–40 lowercase letters, digits, or hyphens.");
        var legacy = configuration.GetValue<bool>("UseLegacyTopologyState");
        if (legacy && id != "default") throw new ArgumentException("Legacy topology state requires TopologyId=default.");
        var root = Path.GetFullPath(configuration["GatewayDataDirectory"] ?? Path.Combine(appHostDirectory, ".data"));
        return new(id, legacy ? root : Path.Combine(root, id), legacy);
    }

    public string Parameter(string name) => Legacy ? name : Id + "-" + name;
    public string Volume(string name) => "wafish-" + (Legacy ? "" : Id + "-") + name;

    public FileStream Acquire()
    {
        Directory.CreateDirectory(DataDirectory);
        try { return new FileStream(Path.Combine(DataDirectory, ".topology.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("This topology data directory is already in use.", error); }
    }
}
