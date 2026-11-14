using System.Reflection;
using System.Text.Json;

namespace WafIsh.Dashboard.Infrastructure.I18n;

/// <summary>
/// Client-side internationalization dictionary for the Dashboard UI.
/// Provides English translations loaded from embedded JSON resource files.
/// Translation files are organized by business domain under Infrastructure/I18n/{locale}/.
/// </summary>
public static class DashboardI18n
{
    /// <summary>Return the supported English UI locale.</summary>
    public static string ResolveLocale(Microsoft.AspNetCore.Http.HttpRequest request) => "en-US";

    private const string ResourcePrefix = "WafIsh.Dashboard.Infrastructure.I18n";

    /// <summary>English translation dictionary.</summary>
    public static readonly Dictionary<string, string> EnUS;

    static DashboardI18n()
    {
        EnUS = LoadLocale("en-US");
    }

    /// <summary>Returns the translation dictionary for the given locale.</summary>
    public static Dictionary<string, string> GetDict(string? locale) => EnUS;

    /// <summary>Serializes the full translation dictionary to JSON for client-side consumption.</summary>
    public static string AllAsJson(string? locale)
    {
        var dict = GetDict(locale);
        return JsonSerializer.Serialize(dict);
    }

    private static Dictionary<string, string> LoadLocale(string locale)
    {
        var dict = new Dictionary<string, string>();
        var assembly = Assembly.GetExecutingAssembly();

        // MSBuild normalises folder names: en-US to en_US
        var localePath = locale.Replace('-', '_');

        foreach (var domain in DomainFiles)
        {
            var resourceName = $"{ResourcePrefix}.{localePath}.{domain}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                // Log a warning but continue — partial translation sets are acceptable
                Console.Error.WriteLine($"[DashboardI18n] Warning: embedded resource not found: {resourceName}");
                continue;
            }

            var domainDict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? new Dictionary<string, string>();

            foreach (var kv in domainDict)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        return dict;
    }

    private static readonly string[] DomainFiles =
    [
        "core.json",
        "clusters.json",
        "routes.json",
        "logs.json",
        "config.json",
        "notif.json",
        "plugins.json",
        "stats.json",
        "ai.json",
        "capabilities.json",
        "webhook.json",
    ];
}
