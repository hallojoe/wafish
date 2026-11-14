# Wafish.Storage.Sqlite: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [StorageServiceCollectionExtensions.cs](StorageServiceCollectionExtensions.cs)
- [SqliteConnectionFactory.cs](SqliteConnectionFactory.cs)
- [SqliteConfigurationSnapshotRepository.cs](SqliteConfigurationSnapshotRepository.cs)
- [Migrations/ISchemaMigration.cs](Migrations/ISchemaMigration.cs)

## Boundaries and invariants

- Keep backup sources unchanged. Explicitly await cancellable migrations on private verification copies before deleting them.

- Write invariant UTC timestamps. Preserve invalid legacy text during normalization migrations.

- Compare proxy log timestamps as UTC instants with the indexed `julianday(Timestamp)` expression.

- Add a new migration for schema changes; preserve existing migration history.
- Use bound parameters for user-controlled SQL values.
- Preserve SQLite3MC initialization and legacy encrypted fixture compatibility.
- Test failed snapshot writes for full rollback and unchanged active configuration.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Storage.Sqlite/Wafish.Storage.Sqlite.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --storage
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Compare every persisted field during differential writes. Keep deletion, insertion, binding renames, and version changes transactional.
