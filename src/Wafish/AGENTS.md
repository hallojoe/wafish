# Wafish: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [Extensions/WafishServiceCollectionExtensions.cs](Extensions/WafishServiceCollectionExtensions.cs)
- [Services/DynamicYarpConfigService.cs](Services/DynamicYarpConfigService.cs)
- [Services/ConfigManagerBase.cs](Services/ConfigManagerBase.cs)
- [Services/GatewaySnapshotCompiler.cs](Services/GatewaySnapshotCompiler.cs)

## Boundaries and invariants

- Keep precommit work cancellable. Retain commit ownership from persistence through activation and audit.

- Keep discovery stale-policy execution fields aligned with the plugin schema and documented defaults.

- Determine orphan clusters from the final route set after every requested batch deletion.

- Keep this project independent of Runtime, Administration, and ReactUi.
- Preserve stable route and cluster identities during renames.
- A failed storage write must leave active configuration unchanged.
- Keep persisted and active versions distinct when activation fails.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish/Wafish.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
