# Wafish.Regression: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [Program.cs](Program.cs)
- [ReviewDeploymentChecks.cs](ReviewDeploymentChecks.cs)
- [ReviewAccountingChecks.cs](ReviewAccountingChecks.cs)
- [ConfigurationTransactionChecks.cs](ConfigurationTransactionChecks.cs)
- [StorageRepositoryChecks.cs](StorageRepositoryChecks.cs)
- [SonarRegressionChecks.cs](SonarRegressionChecks.cs)

## Boundaries and invariants

- Run `--protocol-properties` for generated WAF, cache, compression, and metric checks. Retain corpus provenance.

- Use `--list`, `--case`, and `--group` for registry selections. Retain stable scenario names.
- Run `--fault-schedules --seed 91` to replay named barriers; retain JSON traces on failures.
- Run `--ui-fixtures` after changes to browser-host ownership or profiles.

- Run `--contracts` for mutation responses, registration outcomes, wire compatibility, and lifecycle contracts.

- Run `--lifetime-stress` for replacement, unload, probe contention, and limiter lifetime changes.

- Run `--storage-reliability` for migration faults, forced-process WAL recovery, pagination, and backup command checks.

- Run `--async-policy` for focused configuration cancellation and retry deadline checks.

- Run `--recovery` for deterministic Redis/discovery checks and `--redis` for isolated live Redis recovery.

- Register new checks in Program.cs and return a failing process code when assertions fail.
- Use isolated temporary resources and dispose hosts and database connections.
- Use controlled synchronization for races instead of increasing arbitrary delays.
- Preserve storage fixture provenance and the route contract snapshot.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build tests/Wafish.Regression/Wafish.Regression.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Run `--benchmarks` after benchmark changes. Build the complete Release solution before launching its child hosts.

Use `--sample-deployment` and `--sample-contracts` for sample host changes.
The real topology script requires the target AppHost to be stopped.
