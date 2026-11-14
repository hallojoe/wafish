using System.Diagnostics;
using System.Text.Json;

/// <summary>Writes complete case results on every normal runner exit, including focused modes.</summary>
internal sealed class RegressionReport(string suite) : IDisposable
{
    private readonly List<object> _cases = [];
    public void Record(string name, long started, Exception? error = null)
    {
        _cases.Add(new { name, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            status = error is null ? "passed" : "failed", error = error?.ToString() });
    }
    public void Dispose()
    {
        var path = Environment.GetEnvironmentVariable("WAFISH_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, suite, cases = _cases }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
