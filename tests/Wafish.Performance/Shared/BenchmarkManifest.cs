using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wafish.Performance;

internal sealed record BenchmarkManifest(int SchemaVersion, string ConfigurationHash, JsonNode? Configuration)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static BenchmarkManifest Create(object configuration)
    {
        var normalized = Normalize(JsonSerializer.SerializeToNode(configuration, Json));
        var text = normalized?.ToJsonString() ?? "null";
        return new(1, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), normalized);
    }
    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is JsonObject map)
        {
            var result = new JsonObject();
            foreach (var item in map.OrderBy(item => item.Key, StringComparer.Ordinal))
                result[item.Key] = Sensitive(item.Key) ? JsonValue.Create("[redacted]") : Normalize(item.Value);
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(Normalize).ToArray());
        return node?.DeepClone();
    }
    private static bool Sensitive(string key) => key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("connectionString", StringComparison.OrdinalIgnoreCase) || key.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
        || key.Equals("authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("token", StringComparison.OrdinalIgnoreCase);
}
