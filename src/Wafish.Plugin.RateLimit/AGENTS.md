# Wafish.Plugin.RateLimit: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](../../PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [RateLimitPlugin.cs](RateLimitPlugin.cs)
- [Middleware/RateLimitMiddleware.cs](Middleware/RateLimitMiddleware.cs)

## Boundaries and invariants

- Verify algorithm-specific Retry-After values, queue cancellation, and partition churn with `--lifetime-stress`.

- Local counters belong to the runtime store, not static mutable plugin fields.
- Keep algorithm defaults aligned with manifest schemas and compiled execution plans.
- Honor request cancellation while waiting for a permit.
- Verify rejected requests never reach the upstream delegate.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Plugin.RateLimit/Wafish.Plugin.RateLimit.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in the root PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
