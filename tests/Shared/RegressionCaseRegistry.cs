using System.Diagnostics;
using System.Text.Json;

/// <summary>Registers named cases without executing fixtures, then runs one explicit selection.</summary>
internal sealed class RegressionCaseRegistry(RegressionReport report, TextWriter? output = null, TextWriter? errors = null)
{
    private readonly List<Case> _cases = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly TextWriter _output = output ?? Console.Out;
    private readonly TextWriter _errors = errors ?? Console.Error;
    private string _group = "core";
    public IReadOnlyList<(string Name, string Group)> Cases => _cases.Select(c => (c.Name, c.Group)).ToArray();

    public async Task DefineGroupAsync(string group, Func<Func<string, Func<Task>, Task>, Task> define)
    {
        var previous = _group;
        _group = group;
        try { await define(AddAsync); }
        finally { _group = previous; }
    }
    public void Add(string name, Action action) => AddAsync(name, () => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();
    public Task AddAsync(string name, Func<Task> action)
    {
        if (string.IsNullOrWhiteSpace(name) || !_names.Add(name)) throw new InvalidOperationException("Duplicate or empty regression case: " + name);
        _cases.Add(new(name, _group, action));
        return Task.CompletedTask;
    }

    public async Task<int> RunAsync(string[] args)
    {
        int? seed = null;
        var options = args.ToList();
        var seedIndex = options.IndexOf("--seed");
        if (seedIndex >= 0)
        {
            if (seedIndex + 1 >= options.Count || !int.TryParse(options[seedIndex + 1], out var parsed))
            { _errors.WriteLine("--seed requires a 32-bit integer."); return 2; }
            seed = parsed; options.RemoveRange(seedIndex, 2); args = options.ToArray();
        }
        IReadOnlyList<Case> selected;
        if (args is ["--list"])
        {
            _output.WriteLine(JsonSerializer.Serialize(_cases.Select(c => new { name = c.Name, group = c.Group }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length == 0) selected = _cases.Where(c => c.Group != "redis-live").ToArray();
        else if (args is ["--case", var name]) selected = _cases.Where(c => c.Name == name).ToArray();
        else if (args is ["--group", var group]) selected = _cases.Where(c => c.Group == group).ToArray();
        else if (args is [var flag] && flag.StartsWith("--", StringComparison.Ordinal))
            selected = _cases.Where(c => c.Group == (flag == "--redis" ? "redis-live" : flag[2..])).ToArray();
        else selected = [];
        if (selected.Count == 0)
        {
            _errors.WriteLine("No regression cases matched. Use --list, --case <name>, or --group <group>.");
            return 2;
        }
        var failures = 0;
        foreach (var item in selected)
        {
            using var diagnostics = new RegressionCaseDiagnostics(item.Name, seed);
            var started = Stopwatch.GetTimestamp();
            Exception? failure = null;
            try { await item.Action(); _output.WriteLine("PASS: " + item.Name); }
            catch (Exception error) { failure = error; failures++; _errors.WriteLine(item.Name + ": " + error.Message); }
            report.Record(item.Name, started, failure, item.Group);
        }
        if (failures > 0) _errors.WriteLine($"Regression checks failed: {failures}");
        else _output.WriteLine("All regression checks passed.");
        return failures == 0 ? 0 : 1;
    }
    private sealed record Case(string Name, string Group, Func<Task> Action);
}
