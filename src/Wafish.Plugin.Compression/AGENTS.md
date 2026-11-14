# Wafish.Plugin.Compression: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [CompressionPlugin.cs](CompressionPlugin.cs)
- [CompressionMiddleware.cs](CompressionMiddleware.cs)

## Boundaries and invariants

- Unknown-length thresholds above 64 KiB bypass compression independently of write chunk size.

- Keep proxy compression inside the response-cache boundary.
- Preserve Vary, Content-Encoding, and length semantics.
- Keep HEAD, ranges, existing encodings, and bodyless responses in the test matrix.
- Bound any prefix buffering before choosing an output stream.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.Plugin.Compression/Wafish.Plugin.Compression.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
