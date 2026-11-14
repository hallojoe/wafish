using System.Text.Json;

internal static class RegressionRegistryChecks
{
    public static Task RunAsync(Func<string, Func<Task>, Task> run) => run(
        "Runner registry: dry listing, unique names, selections, exit codes, and complete reports remain stable", async () =>
        {
            using var report = new RegressionReport("registry-fixture", writeFile: false);
            using var output = new StringWriter(); using var errors = new StringWriter();
            var registry = new RegressionCaseRegistry(report, output, errors);
            var ran = new List<string>();
            int? observedSeed = null;
            await registry.DefineGroupAsync("one", add => add("first", () => { observedSeed = RegressionCaseDiagnostics.Current?.Seed; ran.Add("first"); return Task.CompletedTask; }));
            await registry.DefineGroupAsync("two", async add =>
            {
                await add("second", () => { ran.Add("second"); throw new IOException("expected failure"); });
                await add("third", () => { ran.Add("third"); return Task.CompletedTask; });
            });
            await registry.DefineGroupAsync("redis-live", add => add("external", () => throw new Exception("Unselected external case ran.")));
            try { await registry.AddAsync("first", () => Task.CompletedTask); throw new Exception("Duplicate case names were accepted."); }
            catch (InvalidOperationException) { }
            Check(await registry.RunAsync(["--list"]) == 0 && ran.Count == 0, "Listing started test work.");
            using (var listing = JsonDocument.Parse(output.ToString())) Check(listing.RootElement.GetArrayLength() == 4, "Listing lost a registered case.");
            Check(await registry.RunAsync(["--unknown"]) == 2 && ran.Count == 0, "Unknown selection ran the default suite.");
            Check(await registry.RunAsync(["--seed", "bad"]) == 2 && ran.Count == 0, "Invalid seed ran test work.");
            Check(await registry.RunAsync(["--seed"]) == 2 && ran.Count == 0, "Missing seed ran test work.");
            Check(await registry.RunAsync(["--case", "first", "--seed", "91"]) == 0 && ran.SequenceEqual(["first"]) && observedSeed == 91, "Exact selection lost its seed or executed another case.");
            ran.Clear();
            Check(await registry.RunAsync(["--two"]) == 1 && ran.SequenceEqual(["second", "third"]), "Failure skipped a later case or changed focused exit behavior.");
            ran.Clear();
            Check(await registry.RunAsync([]) == 1 && ran.SequenceEqual(["first", "second", "third"]), "Default suite duplicated or skipped a case.");
            using var json = JsonDocument.Parse(report.Serialize());
            var cases = json.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            Check(cases.Length == 6 && cases.Count(c => c.GetProperty("status").GetString() == "failed") == 2,
                "Report omitted passing or failing executions.");
            Check(cases.All(c => c.GetProperty("durationMs").GetDouble() >= 0 && c.GetProperty("group").GetString() is "one" or "two")
                && cases.Where(c => c.GetProperty("status").GetString() == "failed").All(c => c.GetProperty("error").GetString()!.Contains("expected failure")),
                "Report lost duration, group, or failure details.");
        });
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
