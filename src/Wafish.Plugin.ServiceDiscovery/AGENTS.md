# Wafish.Plugin.ServiceDiscovery: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](../../PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [HttpServiceDiscoveryPlugin.cs](HttpServiceDiscoveryPlugin.cs)
- [ServiceDiscoveryRefreshService.cs](ServiceDiscoveryRefreshService.cs)

## Boundaries and invariants

- Keep failure and freshness state per binding. Successful peers must not clear another binding failure.
- Apply stale rejection only to runtime destinations through the shared publication gate.
- Preserve default retention. Distinguish valid empty results from malformed provider responses.

- The canonical plugin ID is service-discovery.
- Do not open discovery connections without an enabled cluster binding.
- Preserve the last valid configuration when discovery cannot provide a valid replacement.
- Review provider parsers, manifest enums, and compiled configuration together.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Plugin.ServiceDiscovery/Wafish.Plugin.ServiceDiscovery.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in the root PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Run `--group need-six` for EndpointSlice changes. Retain pagination bounds, condition defaults, and explicit credential access.
