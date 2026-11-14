using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static partial class ProtocolPropertyChecks
{
    private static async Task WafCorpusAsync()
    {
        var corpus = JsonSerializer.Deserialize<CorpusCase[]>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures/waf/corpus.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var unexpected = new List<string>(); var rejected = new HashSet<string>(); var missed = new HashSet<string>();
        foreach (var item in corpus)
        foreach (var chunk in new[] { 1, 7, 64 })
        {
            var bytes = Encoding.UTF8.GetBytes(item.Body ?? "");
            var result = await WafBodyChecks.Invoke(bytes, 4096, path: item.Path ?? "/waf-test", query: item.Query, contentType: item.ContentType ?? "application/json; charset=utf-8", chunkSize: chunk);
            var blocked = result.Status == 403;
            if (item.Kind == "attack" && !blocked) missed.Add(item.Name);
            if (item.Kind == "benign" && blocked) rejected.Add(item.Name);
            if (blocked != (item.Kind == "attack" || item.KnownRejection)) unexpected.Add(item.Name + ": " + result.Status);
            if (!blocked) Check(result.Status == 200 && result.Forwarded!.SequenceEqual(bytes), "WAF changed accepted bytes: " + item.Name);
        }
        var report = new { name = "waf-corpus", benign = corpus.Count(c => c.Kind == "benign"), attacks = corpus.Count(c => c.Kind == "attack"), rejectedBenign = rejected.Order().ToArray(), missedAttacks = missed.Order().ToArray() };
        Console.WriteLine("WAF corpus: " + JsonSerializer.Serialize(report));
        RegressionCaseDiagnostics.Current?.Traces.Enqueue(JsonSerializer.SerializeToElement(report));
        Check(unexpected.Count == 0, "Unexpected WAF corpus outcomes: " + string.Join("; ", unexpected.Distinct()));
    }
    private static async Task WafFuzzAsync()
    {
        var count = 0; var watch = Stopwatch.StartNew();
        foreach (var seed in DeterministicFaultSchedule.Seeds)
        {
            var random = new Random(seed);
            string[] units = ["a", "é", "東京", "🌍", "&", "=", "%", "%2", "%ZZ", " ", "\n", "\\", "123", "_"];
            for (var index = 0; index < 128; index++)
            {
                var value = string.Concat(Enumerable.Range(0, random.Next(1, 90)).Select(_ => units[random.Next(units.Length)]));
                var bytes = Encoding.UTF8.GetBytes(value);
                var chunk = random.Next(1, 24);
                var path = "/assets/" + Uri.EscapeDataString(value) + (index % 2 == 0 ? "%ZZ" : "");
                var result = await WafBodyChecks.Invoke(bytes, bytes.Length, path: path, query: "?q=" + Uri.EscapeDataString(value), chunkSize: chunk);
                Check(result.Status == 200 && result.Forwarded!.SequenceEqual(bytes), $"WAF seed {seed}, case {index}, chunk {chunk} rejected or changed generated UTF-8.");
                var invalid = await WafBodyChecks.Invoke([.. bytes, 0xff], 4096, chunkSize: chunk);
                Check(invalid.Status == 400 && invalid.Forwarded is null, $"WAF seed {seed}, case {index} accepted malformed UTF-8.");
                var limited = await WafBodyChecks.Invoke(bytes, bytes.Length - 1, chunkSize: chunk);
                Check(limited.Status == 413 && limited.Forwarded is null, $"WAF seed {seed}, case {index} counted characters instead of bytes.");
                count += 3;
            }
        }
        foreach (var first in new[] { ".", "%2e", "%2E", "%252e" })
        foreach (var second in new[] { ".", "%2e", "%252e" })
        foreach (var separator in new[] { "/", "\\", "%2f", "%5C", "%252f", "%255c" })
        {
            var path = "/assets/" + first + second + separator + "secret";
            Check((await WafBodyChecks.Invoke([], 4096, path: path)).Status == 403, "Mixed traversal encoding was not rejected: " + path);
            count++;
        }
        Check((await WafBodyChecks.Invoke([], 4096, path: "/%252e%252e%25252fsecret")).Status == 403, "Double-encoded traversal marker lost conservative rejection.");
        count++;
        var longValue = Encoding.UTF8.GetBytes("<" + new string('a', 90_000));
        var bounded = await WafBodyChecks.Invoke(longValue, 100_000, chunkSize: 17);
        Check(bounded.Status is 200 or 503, "Long nonmatching text violated the bounded regex policy.");
        Check(watch.Elapsed < TimeSpan.FromSeconds(20), "Generated WAF decoding did not terminate within its watchdog.");
        Console.WriteLine($"WAF fuzz: {count + 1} bounded executions across selected seeds.");
    }
    private sealed record CorpusCase(string Name, string Kind, string? Body, string? Query, string? Path, string? ContentType, bool KnownRejection);
}
