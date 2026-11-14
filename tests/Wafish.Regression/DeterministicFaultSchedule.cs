using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>Named barriers and a recorded seed replace timing-based fault injection.</summary>
internal sealed class DeterministicFaultSchedule(string name, int seed)
{
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly List<string> _steps = [];
    private readonly object _gate = new();
    public int Seed => seed;
    public void Record(string step) { lock (_gate) _steps.Add(step); }
    public async Task HoldAsync(string step, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(step, _ => new());
        Record("arrive:" + step); gate.Arrived.TrySetResult();
        try { await gate.Released.Task.WaitAsync(cancellationToken); Record("continue:" + step); }
        catch (OperationCanceledException) { Record("cancel:" + step); throw; }
    }
    public Task WaitAsync(string step) => _gates.GetOrAdd(step, _ => new()).Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
    public void Release(string step)
    {
        Record("release:" + step); _gates.GetOrAdd(step, _ => new()).Released.TrySetResult();
    }
    public async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        finally
        {
            var context = RegressionCaseDiagnostics.Current;
            string[] operations; lock (_gate) operations = _steps.ToArray();
            var trace = JsonSerializer.SerializeToElement(new { name, seed, operations,
                replay = "dotnet run --project tests/Wafish.Regression -c Release -- --case " + JsonSerializer.Serialize(context?.CaseName) + " --seed " + seed });
            context?.Traces.Enqueue(trace);
        }
    }
    public string[] Order(params string[] operations)
    {
        var result = operations.ToArray(); var random = new Random(seed);
        for (var i = result.Length - 1; i > 0; i--) { var j = random.Next(i + 1); (result[i], result[j]) = (result[j], result[i]); }
        return result;
    }
    public static IEnumerable<int> Seeds => RegressionCaseDiagnostics.Current?.Seed is { } selected ? [selected] : [17, 91, 2026];
    private sealed class Gate
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
