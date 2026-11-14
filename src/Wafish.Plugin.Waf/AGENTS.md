# Wafish.Plugin.Waf: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [WafPlugin.cs](WafPlugin.cs)
- [Middleware/WafMiddleware.cs](Middleware/WafMiddleware.cs)
- [Middleware/IWafRuleChecker.cs](Middleware/IWafRuleChecker.cs)
- [Helpers/IpMatcher.cs](Helpers/IpMatcher.cs)

## Boundaries and invariants

- Preserve the synthetic benign/attack corpus and report both rejection and miss counts. Keep explicit known false positives.

- Keep inspection effective for chunked requests and missing Content-Length.
- Count limits in bytes and preserve inspected request bodies for downstream consumers.
- Do not trust forwarding headers unless the host has configured trusted proxies.
- Keep malformed decoding, read failures, and cancellation covered by regression checks.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Plugin.Waf/Wafish.Plugin.Waf.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
