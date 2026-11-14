# Wafish.Administration.Regression: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [Program.cs](Program.cs)
- [AdministrationRegressionSuite.cs](AdministrationRegressionSuite.cs)
- [AdministrationRegression.Fixture.cs](AdministrationRegression.Fixture.cs)
- [Wafish.Administration.Regression.csproj](Wafish.Administration.Regression.csproj)

## Boundaries and invariants

- Keep every mapped administration action in the authorization matrix. Test revoked tokens against both APIs and existing hubs.

- Keep this host free of the removed dashboard assembly and Razor assets.
- Generate temporary credentials and isolate database and two-factor files.
- Assert HTTP status and side effects, not only response text.
- Verify cookies, origins, and realtime authorization when changing authentication.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build tests/Wafish.Administration.Regression/Wafish.Administration.Regression.csproj -c Release
dotnet run --project tests/Wafish.Administration.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Use `--need-seven` for exclusion audits and previews, operational endpoints, and provider backup contracts.
Keep added endpoints in the shared dashboard route inventory.
