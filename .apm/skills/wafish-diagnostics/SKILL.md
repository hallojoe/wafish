---
name: wafish-diagnostics
description: Investigate Wafish routing, configuration activation, plugin health, storage, request logs, or runtime performance. Use for observed gateway failures; use wafish-build for compilation failures.
---

# Diagnose a Wafish gateway

1. Identify the affected host, route, symptom, and time window from available evidence.
   Read `docs/architecture.md`, `docs/guide/operations.md`, and the relevant component guidance as needed.
   Inspect the current branch before assuming an optional profile, endpoint, or newer feature exists.
2. If Aspire owns the host, use `aspire-monitoring` and the exact AppHost target.
   Start with resource state, logs, and traces. Use `aspire-orchestration` for lifecycle changes.
   The repository AppHost is `Wafish.AppHost/Wafish.AppHost.csproj`.
   Check CLI help for version-sensitive commands. Do not replace Aspire ownership with arbitrary process termination.
3. Correlate gateway and backend observations using trace IDs, route identities, and backend instance headers.
   Distinguish a rejected request, failed forwarding attempt, and failed response delivery.
   Do not expose authentication headers, cookies, bodies, or configuration credentials in diagnostic output.
4. For configuration problems, compare persisted and active revisions and inspect activation outcomes.
   A successful storage write does not prove activation succeeded. Preserve both revisions and the original error.
   Do not repeatedly apply mutations or overwrite stored configuration merely to clear a warning.
5. For plugin failures, inspect bindings, effective order, health, and owned runtime resources.
   Consult the plugin README before changing retry, circuit, cache, limiter, or discovery behavior.
   For SQLite problems, inspect a copy or use the storage tool's documented read-only checks.
   Never remove the live database, WAL files, or persistent volumes as routine recovery.
6. Use `dotnet-trace-collect` or `dump-collect` when runtime evidence requires them.
   Scope captures to the identified process and a bounded duration. Treat captured artifacts as potentially sensitive.
   Use `analyzing-dotnet-performance` only when the evidence points to relevant hot paths.

## Validation

Connect each conclusion to an observed request, state transition, log, trace, or captured artifact.
Separate facts from hypotheses. Reproduce the symptom and verify the fix through the affected path when practical.
Stop only resources started by this task. Preserve user-owned topology, credentials, and data.
