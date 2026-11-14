using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Wafish.Models;

namespace Wafish.Plugin.Waf;

internal static class WafFieldExclusions
{
    internal static void Validate(WafBindingOptions options)
    {
        string[] rules = ["sql-injection", "xss", "path-traversal", "ip-access"];
        if (options.DetectionOnlyRules is null || options.DetectionOnlyRules.Count > 4 || options.DetectionOnlyRules.Any(r => !rules.Contains(r))
            || options.Exclusions is null || options.Exclusions.Count > 32 || options.Exclusions.Any(e => e is null
                || e.RuleId is not ("sql-injection" or "xss") || e.Target is not ("Query" or "Form" or "Json")
                || string.IsNullOrWhiteSpace(e.Field) || e.Field.Length > 256 || e.Field.Contains('*')
                || e.Target == "Json" && !e.Field.StartsWith('/')))
            throw new InvalidOperationException("WAF rules require known IDs and bounded, exact parsed-field exclusions.");
    }

    internal static WafCheckContext ForRule(WafCheckContext source, string rule)
    {
        var exclusions = source.Options.Exclusions.Where(e => e.RuleId == rule).ToArray();
        if (exclusions.Length == 0) return source;
        var query = source.DecodedQueryString;
        var body = source.DecodedBodyText;
        if (exclusions.Any(e => e.Target == "Query")) query = FilterPairs(source.HttpContext.Request.QueryString.Value ?? "", "Query");
        var type = source.HttpContext.Request.ContentType ?? "";
        if (body is not null && type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) && exclusions.Any(e => e.Target == "Form"))
            body = FilterPairs(source.RawBodyText ?? body, "Form");
        if (body is not null && (type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || type.Split(';')[0].EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            && exclusions.Any(e => e.Target == "Json"))
        {
            using var document = JsonDocument.Parse(source.RawBodyText ?? body, new JsonDocumentOptions { MaxDepth = 32 });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                Write(document.RootElement, "", writer);
            body = Encoding.UTF8.GetString(stream.ToArray());
        }
        return new() { HttpContext = source.HttpContext, Options = source.Options, RawRequestPath = source.RawRequestPath,
            DecodedQueryString = query, DecodedBodyText = body, RawBodyText = source.RawBodyText };

        string FilterPairs(string text, string target) => string.Join('&', QueryHelpers.ParseQuery(text)
            .SelectMany(p => p.Value.Select(v => p.Key + "=" + (exclusions.Any(e => e.Target == target && e.Field == p.Key) ? "" : v))));
        void Write(JsonElement value, string pointer, Utf8JsonWriter writer)
        {
            if (exclusions.Any(e => e.Target == "Json" && e.Field == pointer))
            {
                if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) throw new JsonException("Only scalar fields can be excluded.");
                writer.WriteStringValue(""); return;
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, pointer + "/" + property.Name.Replace("~", "~0").Replace("/", "~1"), writer);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); var index = 0;
                foreach (var item in value.EnumerateArray()) Write(item, pointer + "/" + index++, writer);
                writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }
}
