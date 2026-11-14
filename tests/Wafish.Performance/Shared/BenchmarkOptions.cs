namespace Wafish.Performance;

internal static class BenchmarkOptions
{
    public static string Read(string[] args, string name, string fallback)
    {
        var prefix = name + "=";
        var inline = args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (inline is not null) return inline[prefix.Length..];
        var index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(name + " requires a value.");
        return args[index + 1];
    }

    public static Uri LoopbackAddress(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException(name + " requires an HTTP loopback origin without credentials, a path, query, or fragment.");
        return uri;
    }
}
