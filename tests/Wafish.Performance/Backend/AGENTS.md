# Backend: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [Program.cs](Program.cs)
- [Backend.csproj](Backend.csproj)

## Boundaries and invariants

- Keep control endpoints on loopback and exclude them from workload counters.
- Reset flaky counters between scenarios.
- Keep payload and delay bounds explicit.
- Change matching runner assumptions when endpoint behavior changes.

## Repository conventions

- Target .NET 10; SDK selection is in [../../../global.json](../../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build tests/Wafish.Performance/Backend/Backend.csproj -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Preserve explicit loopback addresses, bounded preflight, and configuration hashes.
Never delete databases outside the current session directory.
Verify changes with the regression `--benchmarks` group after a Release solution build.
