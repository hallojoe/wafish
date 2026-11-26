namespace Wafish.Performance;

internal sealed record BenchmarkPluginSelection(string Name, string[] Plugins);

internal static class BenchmarkPluginMatrix
{
    public const int MaximumSets = 8;
    public const string DefaultSets = "none;waf;waf+rate-limit;response-cache+compression";
    public static BenchmarkPluginSelection[] Parse(string text)
    {
        if (text.Length > 2048) throw new ArgumentException("Plugin matrix exceeds its input bound.");
        var parts = text.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > MaximumSets) throw new ArgumentException("Select one to eight explicit plugin sets.");
        var sets = parts.Select(ParseSet).ToArray();
        if (sets.Select(set => set.Name).Distinct(StringComparer.Ordinal).Count() != sets.Length)
            throw new ArgumentException("Duplicate plugin sets are not allowed.");
        return sets;
    }
    public static BenchmarkPluginSelection ParseSet(string text)
    {
        if (text == "none") return new("none", []);
        var plugins = text.Split('+', StringSplitOptions.TrimEntries);
        if (plugins.Length is < 1 or > 6 || plugins.Distinct(StringComparer.Ordinal).Count() != plugins.Length)
            throw new ArgumentException("A plugin set must contain one to six distinct plugins, or none.");
        foreach (var plugin in plugins) _ = Configuration(plugin);
        Array.Sort(plugins, StringComparer.Ordinal);
        return new(string.Join('+', plugins), plugins);
    }
    public static string Configuration(string plugin) => plugin switch
    {
        "waf" => """{"enabled":true}""",
        "rate-limit" => """{"enabled":true,"algorithm":"FixedWindow","permitLimit":1000000,"window":"1s","queueLimit":0,"partitionKey":"Global"}""",
        "response-cache" => """{"enabled":true,"ttlSeconds":60,"maxBodyBytes":1048576}""",
        "compression" => """{"enabled":true,"minResponseSize":0,"compressionLevel":"Fastest"}""",
        "traffic-metrics" => """{"enabled":true,"includeRequestBytes":true,"includeResponseBytes":true}""",
        "request-retry" => """{"enabled":true,"maxRetries":2,"backoffBaseMs":10,"backoffJitterMs":0,"timeoutSeconds":5,"totalTimeoutSeconds":10}""",
        _ => throw new ArgumentException("Unsupported benchmark plugin: " + plugin)
    };
}
