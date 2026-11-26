using System.Text.Json;
using System.Text.Json.Serialization;
using Wafish.Models;
using Wafish.Storage;
using Wafish.Storage.Entities;

namespace Wafish.Services;

internal static class RateLimitQuotaGroups
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Validate(IEnumerable<PluginBindingSnapshot> bindings)
    {
        var groups = new Dictionary<string, RateLimitExecutionConfig>(StringComparer.Ordinal);
        foreach (var binding in bindings.Where(b => b.Scope == PluginBindingScope.Route && b.PluginId == "rate-limit"))
        {
            using var document = JsonDocument.Parse(binding.ConfigJson);
            var groupField = document.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("quotaGroup", StringComparison.OrdinalIgnoreCase));
            if (groupField.Value.ValueKind == JsonValueKind.Undefined) continue;
            if (groupField.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("quotaGroup must be a string.");
            var name = groupField.Value.GetString()!;
            if (name.Length == 0) continue;
            if (name.Length > 64 || !char.IsAsciiLetterOrDigit(name[0]) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
                throw new ArgumentException("quotaGroup must contain 1..64 ASCII letters, digits, periods, underscores, or hyphens; start with a letter or digit.");
            var config = JsonSerializer.Deserialize<RateLimitExecutionConfig>(binding.ConfigJson, Json)!;
            if (!config.Enabled) continue;
            config = config with { RouteUid = string.Empty, QuotaGroup = string.Empty };
            if (groups.TryGetValue(name, out var existing) && existing != config)
                throw new ArgumentException("Routes in quotaGroup '" + name + "' must use identical quota and partition settings.");
            groups[name] = config;
        }
    }
}
