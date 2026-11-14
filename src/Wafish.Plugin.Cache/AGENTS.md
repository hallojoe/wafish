# Wafish.Plugin.Cache: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [ResponseCachePlugin.cs](ResponseCachePlugin.cs)
- [ResponseCacheMiddleware.cs](ResponseCacheMiddleware.cs)
- [ResponseCacheStore.cs](ResponseCacheStore.cs)
- [BoundedCaptureStream.cs](BoundedCaptureStream.cs)

## Boundaries and invariants

- Run generated protocol properties after changes to Age, freshness, Vary, or request cache directives.

- Measure cache age with the injected monotonic clock, never wall-clock subtraction.

- Keep deprecated variant switches accepted but ineffective; their schema must state the conservative key behavior.

- Keep cache keys isolated by host, query, relevant headers, and configuration generation.
- Preserve bypass rules for credentials, cookies, private responses, and conditional requests.
- The memory budget includes pending captures and entries still being served.
- Do not buffer an entire response before enforcing its size limit.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Plugin.Cache/Wafish.Plugin.Cache.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.

Charge both buffers during compaction. Keep evicted entries charged until all readers finish.
