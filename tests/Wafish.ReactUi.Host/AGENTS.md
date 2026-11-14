# Wafish.ReactUi.Host: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [Program.cs](Program.cs)
- [Wafish.ReactUi.Host.csproj](Wafish.ReactUi.Host.csproj)

## Boundaries and invariants

- Keep fixture options, lifecycle ownership, and process-selected fault profiles in the test host.
- Do not add HTTP fault-control endpoints. Arm failures only after normal startup.
- Preserve unpooled fixture connections and private directory ownership.

- Keep test certificates in memory. Run browser tests through the parent runner to clean failed and interrupted fixtures.

- Keep selectable path bases and nested prefixes covered across all supported browser authentication modes.

- Require UI_TEST_PASSWORD; never add a fixed fallback credential.
- Keep each process on a separate port and temporary data directory.
- Preserve the runtime check that excludes Razor and the old dashboard assembly.
- Do not make this test fixture depend on the running sample gateway.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build tests/Wafish.ReactUi.Host/Wafish.ReactUi.Host.csproj -c Release
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
