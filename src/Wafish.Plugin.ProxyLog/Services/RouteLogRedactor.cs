using System.Text;
using System.Text.Json;
using Wafish.Plugin.ProxyLog.Models;

namespace Wafish.Plugin.ProxyLog.Services;

// JSON Pointer paths support wildcard segments. Bounds also protect manually published bindings.
internal sealed class RouteLogRedactor
{
    private const string Mask = "***REDACTED***";
    private readonly HashSet<string> _headers;
    private readonly string[][] _paths;
    private readonly bool _invalid;

    public RouteLogRedactor(string[]? headers, string[]? paths)
    {
        headers ??= [];
        paths ??= [];
        _invalid = headers.Length > 64 || paths.Length > 64 ||
            headers.Any(h => string.IsNullOrEmpty(h) || h.Length > 128) || paths.Any(p =>
                p is null || p.Length > 256 || !p.StartsWith('/') || p.Count(c => c == '/') > 16 ||
                p.Replace("~0", "").Replace("~1", "").Contains('~'));
        _headers = new(headers.Take(64), StringComparer.OrdinalIgnoreCase);
        _paths = _invalid ? [] : paths.Select(p => p.Split('/').Skip(1)
            .Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray()).ToArray();
    }

    public HeaderList? Headers(HeaderList? headers)
    {
        if (headers is null) return null;
        for (var i = 0; i < headers.Count; i++)
            if (_invalid || _headers.Contains(headers[i].Key)) headers[i] = new(headers[i].Key, Mask);
        return headers;
    }

    public string? Body(string? body)
    {
        if (string.IsNullOrEmpty(body)) return body;
        if (_invalid) return Mask;
        if (_paths.Length == 0) return body;
        try
        {
            using var document = JsonDocument.Parse(body);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) Write(writer, document.RootElement, [], _paths);
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (JsonException) { return Mask; }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, List<string> location, string[][] paths)
    {
        if (paths.Any(p => p.Length == location.Count && p.Select((s, i) => s == "*" || s == location[i]).All(x => x)))
        {
            writer.WriteStringValue(Mask);
            return;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                location.Add(property.Name);
                Write(writer, property.Value, location, paths);
                location.RemoveAt(location.Count - 1);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                location.Add((index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Write(writer, item, location, paths);
                location.RemoveAt(location.Count - 1);
            }
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
