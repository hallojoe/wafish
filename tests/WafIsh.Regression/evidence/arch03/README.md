# ARCH-03 verification

Text paths and labels were updated for WafIsh and English. Historical results below remain from their original runs.

Completed: 2026-11-14. Owner: Codex.
Tested tree: commit `6ba871a0f0304957d3f178931434f5e9fc6cf16b`, plus the uncommitted implementation changes.
SDK: 10.0.403. Target: net10.0.

## Automated checks

| Command | Result |
|---|---|
| `dotnet build WafIsh.slnx --nologo` | Zero warnings and errors; [Debug output](build-debug.txt) |
| `dotnet build WafIsh.slnx -c Release --nologo` | Zero warnings and errors; [Release output](build-release.txt) |
| `dotnet run --project tests/WafIsh.Regression -c Release --no-build` | 44 checks pass; [output](regression.txt) |
| `dotnet run --project tests/WafIsh.Administration.Regression -c Release --no-build` | Eight checks pass; [output](administration.txt) |
| `dotnet publish samples/SampleGateway/SampleGateway.csproj -c Release --no-build -o /tmp/aneiang-arch03-publish --nologo` | Succeeded |

The baseline build and all 42 original checks passed before extraction.
Before moving files, `--dashboard-routes` captured 102 controller method/path pairs.
The main suite now compares those pairs against [the saved contract](../../contracts/dashboard-routes.json).
It also verifies that referencing the dashboard assembly does not implicitly register its controllers or Razor engine.

The separate administration executable references administration and SQLite only.
It verifies these behaviors:

- No dashboard assembly is deployed. No Razor engine or presentation endpoints are registered.
- Existing shared English translations load from embedded resources.
- Missing or incorrect API keys receive HTTP 401.
- Query APIs, settings, and database downloads work without Razor.
- Cluster and route creation preserve the existing JSON responses.
- Stored configuration survives host restart and remains editable.
- POST login issues JWTs without requiring a Razor login page.
- Traffic subscriptions and overview updates work through real SignalR WebSocket connections.

## Published browser checks

The published sample ran in Production on temporary loopback ports with the custom prefix `/review`.
It used a temporary encrypted database and test credentials. MCP and automatic browser launch were disabled.
The Playwright CLI skill provided browser automation.

- Submitted the login form and reached the overview page.
- Observed a successful overview SignalR connection.
- Opened the cluster editor and saved `architecture-ui-cluster` through the UI.
- Confirmed HTTP 200 from the cluster save API and the new cluster in the rendered table.
- Loaded overview, clusters, and all 12 remaining navigation pages.
- Verified English settings rendering.
- Confirmed logout returns to login and removes authenticated API access.

See [page and locale results](browser-pages.json).
No uncaught page errors occurred during navigation and locale checks.
No application errors appeared in the published host log.

## Remaining limitations

Opening the Monaco editor reports a missing `lib/vs/base/common/worker/simpleWorker.nls.js` file.
The pre-extraction published sample returns HTTP 404 for the same file.
Cluster saving succeeds despite this existing asset issue. UI-01 tracks follow-up.

This step preserves authentication behavior. It does not complete the planned production security changes.
AI/MCP opt-in registration, pipeline separation, and transactional persistence remain separate tasks.
Moved public types require consumer recompilation; see [migration instructions](../../../../docs/architecture.md#consumer-migration).
