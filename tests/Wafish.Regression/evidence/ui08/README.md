# Razor UI removal

Text paths and labels were updated for Wafish and English. Historical results below remain from their original runs.

Desktop and mobile screenshots were refreshed from the English Wafish browser suite on 2026-11-14.

Task: UI-08. Owner: Codex. Date: 2026-11-14.
Tested source: uncommitted working tree based on `6ba871a`.

Removed the Razor project, page controller, views, static assets, hosting wrappers, and React fallback controls.
Sample and performance hosts use runtime and administration registration directly.
Package publishing now includes Runtime, Administration, and ReactUi instead of Dashboard.

All 87 non-page controller contracts remain unchanged.
The contract fixture removes precisely the 15 former Razor GET page routes.
POST login and logout remain available. GET login returns HTTP 405.
Existing authentication settings, API prefixes, and shared service namespaces remain compatible.
Shared plugin metadata and localized API descriptions remain useful without Razor.

## Verification

| Check | Result | Evidence |
|---|---|---|
| Release solution build | Zero warnings and errors | [Build](build-release.log) |
| Main regression suite | 64 passing checks | [Regression](regression.log) |
| Administration regression suite | 19 passing checks | [Administration](administration.log) |
| Frontend unit suite | Nine passing checks | [Unit tests](frontend-unit.log) |
| Browser suite | 17 passing checks | [Browser tests](browser.log) |
| Frontend lint and formatting | Passed | [Lint](frontend-lint.log) |
| Published sample container | No Razor assembly or assets; React and APIs work | [Container](container.log) |

The browser suite covers authentication, native configuration, plugins, history, failures, two-factor authentication, and realtime connections.
It also checks English layouts, keyboard focus, accessibility, and unavailable legacy page routes.
Published-container checks verify the actual sample composition, embedded assets, deep links, and realtime connections.
ProxyOnly deployment keeps React pages, administration APIs, and realtime hubs unavailable.
Test containers were stopped and removed after verification.
Source checks find no Razor project reference, `.cshtml` file, or `.razor` file.

Current screenshots: [desktop](desktop-overview.png), [English mobile](mobile-english.png).

## Commands

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run format:check
npm --prefix src/Wafish.ReactUi/ClientApp test
PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
podman build -f samples/SampleGateway/Dockerfile -t localhost/wafish:verification .
```

Container checks use direct loopback listeners and explicit Development-only anonymous loopback permission.
No production deployment or package publication was performed.
The native SQLCipher blocker existed at this verification point. [SEC-14](../sec14/README.md) subsequently resolves it.
