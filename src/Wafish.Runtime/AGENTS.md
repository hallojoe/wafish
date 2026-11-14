# Wafish.Runtime: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [GatewayRuntimeExtensions.cs](GatewayRuntimeExtensions.cs)
- [GatewayPipelineExtensions.cs](GatewayPipelineExtensions.cs)
- [Infrastructure/Plugin/PluginRuntimeDomain.cs](Infrastructure/Plugin/PluginRuntimeDomain.cs)
- [Infrastructure/Plugin/PluginResourceLifecycleCoordinator.cs](Infrastructure/Plugin/PluginResourceLifecycleCoordinator.cs)

## Boundaries and invariants

- Activation previews dispose candidates without publishing domains or starting background resources.

- Merge lazy audit history with accepted writes before enforcing its capacity. Keep reader snapshots atomic.

- Join accepted audit writes before disposal. Stop resource reconciliation before shutdown cleanup.

- Preserve copied-lease ownership and asynchronous external cleanup. Run the lifetime stress selection after lifecycle changes.

- Preserve custom TimeProvider registrations and both-scope IServiceProviderIsService resolution.

- Retain retirement tasks and join them during shutdown. Reject activation after shutdown starts.

- Keep Administration and ReactUi references out of this project.
- Start plugin resources through lifecycle contracts, not plugin constructors.
- Retain the old domain when preparation or health checks fail.
- Preserve request draining before disposing an old domain.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Runtime/Wafish.Runtime.csproj -c Release
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
