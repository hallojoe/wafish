# Wafish.ReactUi: agent notes

These instructions apply to this project and its subdirectories.
Read [README.md](README.md) for scope and [PLAN.md](PLAN.md) for proposals.
Proposals do not authorize implementation outside the current user request.

## Source map

- [ReactUiExtensions.cs](ReactUiExtensions.cs)
- [Wafish.ReactUi.csproj](Wafish.ReactUi.csproj)
- [ClientApp/src/api/client.ts](ClientApp/src/api/client.ts)
- [ClientApp/tests/browser/workspace.spec.ts](ClientApp/tests/browser/workspace.spec.ts)

## Boundaries and invariants

- Cover each workspace with component tests. Preserve drafts during failed writes and query refreshes.
- Use matching locale query keys for realtime updates. Show assistant loading and query errors.
- Wait for dialog animations before axe measurements. Keep keyboard focus assertions.

- Prefer mutation outcome fields while retaining legacy status compatibility. Do not display unknown revisions.

- Two-factor changes require a new sign-in. Keep the browser lifecycle check aligned with this behavior.

- Preserve PathBase in shell URLs. Validate every manifest asset before embedding or packing.

- Edit frontend source under ClientApp, not generated wwwroot assets.
- Keep API access in the shared client and query helpers.
- Preserve authentication mode handling, request cancellation, and session expiry behavior.
- Do not confuse an empty loading view with a completed mutation in browser tests.

## Repository conventions

- Target .NET 10; SDK selection is in [../../global.json](../../global.json).
- Package versions are centralized in [Directory.Packages.props](../../Directory.Packages.props).
- Do not edit generated `bin`, `obj`, `node_modules`, `coverage`, or `wwwroot` output.
- Follow [security boundaries](../../docs/security.md) when changing access, secrets, or request handling.
- Keep new source and comments in the existing project conventions.

## Verification

Run these commands from the repository root after relevant code changes:

```bash
dotnet build src/Wafish.ReactUi/Wafish.ReactUi.csproj -c Release
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

The regression projects are executable runners. A nonzero exit code means verification failed.
For changed code, maintain at least 80% coverage and add relevant regression checks.
For documentation-only changes, check links and Mermaid rendering instead of starting services.

## Documentation maintenance

Update the README when behavior or scope changes.
Keep future work in PLAN.md until its acceptance checks pass.
Record implementation evidence when completing a plan item.
