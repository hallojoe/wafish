# Wafish.Plugin.Metrics: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](../../PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [MetricsPlugins.cs](MetricsPlugins.cs)
- [PluginMetricsMiddleware.cs](PluginMetricsMiddleware.cs)

## Boundaries and invariants

- Run concurrent protocol properties after changing counter updates, snapshots, or identity retirement.

- Retire obsolete identities and retain the 10,000-series limit for each metric scope.

- Record each measured pipeline attempt once, including retries, exceptions, and cancellations. Keep cancellation counts separate.

- This project contains two plugin IDs, not one.
- Avoid counting a request twice when both route and cluster metrics are enabled.
- Do not add unbounded user-controlled metric dimensions.
- Keep snapshot reads safe during concurrent updates.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Plugin.Metrics/Wafish.Plugin.Metrics.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in the root PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Preserve the [R19–R28 contracts](../../docs/registration-operations-contracts.md) when changing registration or operational controls.
Run the `--group need-seven` regression checks for these contracts.
