# Wafish.Plugin.ProxyLog: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [ProxyLogPlugin.cs](ProxyLogPlugin.cs)
- [YarpRequestCaptureMiddleware.cs](YarpRequestCaptureMiddleware.cs)
- [Services/AsyncLogPersistenceService.cs](Services/AsyncLogPersistenceService.cs)
- [Services/ProxyLogBodyReader.cs](Services/ProxyLogBodyReader.cs)

## Boundaries and invariants

- Normalize history page metadata before querying storage. Calculate recent-log offsets without integer overflow.

- Use the injected clock for lifecycle timers and join timer callbacks before disposing cancellation sources.

- Preserve unfinished batches; account for every persistence-enabled submission as written, pending, or discarded.

- Preserve sanitization before records reach storage or clients.
- Keep capture limits effective for streaming and compressed bodies.
- Report dropped entries; do not silently treat the queue as lossless.
- Check resource shutdown, cancellation, and final draining after persistence changes.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Plugin.ProxyLog/Wafish.Plugin.ProxyLog.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
