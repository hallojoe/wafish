using Wafish.Plugins;

namespace Wafish.Dashboard.Infrastructure.Plugin;

internal static class PluginManifestPolicy
{
    private static readonly string[] BuiltinOrder =
        ["rate-limit", "rate-limit-redis", "request-retry", "response-cache", "compression", "proxy-log", "traffic-metrics", "cluster-metrics", "circuit-breaker"];

    public static IReadOnlyList<PluginManifest> Resolve(IEnumerable<PluginManifest> manifests)
    {
        var nodes = manifests.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var edges = nodes.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        void Edge(string from, string to)
        {
            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to))
                throw new InvalidOperationException($"Plugin ordering requires enabled plugins '{from}' and '{to}'.");
            edges[from].Add(to);
        }
        foreach (var manifest in nodes.Values)
        {
            foreach (var before in manifest.Before ?? []) Edge(manifest.Id, before);
            foreach (var after in manifest.After ?? []) Edge(after, manifest.Id);
            foreach (var capability in manifest.RequiredHostCapabilities ?? [])
            {
                if (capability.MinimumVersion < 1 || !PluginHostCapabilities.Versions.TryGetValue(capability.Name, out var version) || version < capability.MinimumVersion)
                    throw new InvalidOperationException($"Plugin '{manifest.Id}' requires unsupported host capability '{capability.Name}' version {capability.MinimumVersion}.");
            }
        }
        var builtin = BuiltinOrder.Where(nodes.ContainsKey).ToArray();
        for (var i = 1; i < builtin.Length; i++) Edge(builtin[i - 1], builtin[i]);
        var indegree = nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var targets in edges.Values) foreach (var target in targets) indegree[target]++;
        var ordered = new List<PluginManifest>();
        while (ordered.Count < nodes.Count)
        {
            var next = nodes.Values.Where(m => indegree[m.Id] == 0)
                .OrderBy(m => m.Order).ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault();
            if (next is null) throw new InvalidOperationException("Plugin ordering constraints contain a cycle.");
            indegree[next.Id] = -1;
            ordered.Add(next);
            foreach (var target in edges[next.Id]) indegree[target]--;
        }
        return ordered;
    }
}
