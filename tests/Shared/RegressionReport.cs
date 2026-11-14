using System.Diagnostics;
using System.Text.Json;

/// <summary>Writes complete case results on every normal runner exit, including focused modes.</summary>
internal sealed class RegressionReport(string suite, bool writeFile = true) : IDisposable
{
    private readonly List<object> _cases = [];
    public void Record(string name, long started, Exception? error = null, string? group = null)
    {
        _cases.Add(new { name, group, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            status = error is null ? "passed" : "failed", error = error?.ToString(), schedules = RegressionCaseDiagnostics.Current?.Traces.ToArray() ?? [] });
    }
    public string Serialize() => JsonSerializer.Serialize(new { schemaVersion = 1, suite, cases = _cases }, new JsonSerializerOptions { WriteIndented = true });
    public void Dispose()
    {
        var path = Environment.GetEnvironmentVariable("WAFISH_TEST_REPORT");
        if (!writeFile || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize());
    }
}

/// <summary>Case-local replay metadata also flows through awaited worker tasks.</summary>
internal sealed class RegressionCaseDiagnostics : IDisposable
{
    private static readonly AsyncLocal<RegressionCaseDiagnostics?> Active = new();
    private readonly RegressionCaseDiagnostics? _previous;
    public static RegressionCaseDiagnostics? Current => Active.Value;
    public string CaseName { get; }
    public int? Seed { get; }
    public System.Collections.Concurrent.ConcurrentQueue<JsonElement> Traces { get; } = new();
    public RegressionCaseDiagnostics(string caseName, int? seed)
    { CaseName = caseName; Seed = seed; _previous = Active.Value; Active.Value = this; }
    public void Dispose() => Active.Value = _previous;
}
