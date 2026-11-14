# Wafish.Storage.Abstractions: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [IConfigurationSnapshotRepository.cs](IConfigurationSnapshotRepository.cs)
- [Storage/IDbConnectionFactory.cs](Storage/IDbConnectionFactory.cs)
- [Storage/IPluginConfigurationRepository.cs](Storage/IPluginConfigurationRepository.cs)
- [Storage/StableUid.cs](Storage/StableUid.cs)

## Boundaries and invariants

- Atomic receipt support is optional. Existing snapshot providers must remain source compatible.

- Keep history read limits and stable ordering aligned with RepositoryQueryLimits and the provider-neutral conformance suite.

- Keep SQL and native database dependencies out of this project.
- Preserve expected-version checks in the configuration snapshot contract.
- Keep stable UIDs separate from editable route and cluster names.
- Document defaults and cancellation behavior when extending repository methods.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Storage.Abstractions/Wafish.Storage.Abstractions.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Preserve the [R19–R28 contracts](../../docs/need-seven-features.md) when changing registration or operational controls.
Run the `--group need-seven` regression checks for these contracts.
