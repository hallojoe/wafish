# Wafish.AppHost: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [AppHost.cs](AppHost.cs)
- [SonarQubeResources.cs](SonarQubeResources.cs)
- [SonarQubeHealthCheck.cs](SonarQubeHealthCheck.cs)
- [Wafish.AppHost.csproj](Wafish.AppHost.csproj)

## Boundaries and invariants

- Use Aspire CLI commands for lifecycle changes.
- Preserve separate HTTP and HTTP/2 endpoints and stable backend instance identities.
- Keep Redis, the second gateway, and SonarQube opt-in.
- Keep generated secrets out of committed files and diagnostic output.
- Do not remove data volumes to fix a routine startup failure.

## Repository conventions

- Target .NET 10; SDK selection is in [../global.json](../global.json).
- Package versions are centralized in [Directory.Packages.props](../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build Wafish.AppHost/Wafish.AppHost.csproj -c Release
dotnet build Wafish.slnx -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
