# React UI verification

Text paths and labels were updated for Wafish and English. Historical results below remain from their original runs.

Desktop and mobile screenshots were refreshed from the English Wafish browser suite on 2026-11-14.

This records the original migration. [UI-08](../ui08/README.md) subsequently removes the Razor fallback.

Tasks: UI-03 through UI-07, ARCH-10, and SEC-13.
Owner: Codex. Completed: 2026-11-14.
Tested source: working tree based on `6ba871a`. Changes remain uncommitted.

## Results

| Check | Result | Evidence |
|---|---|---|
| Release solution build | Zero warnings and errors | [Build](build-release.log) |
| Main regression suite | 64 passing checks | [Regression](regression.log) |
| Administration regression suite | 19 passing checks | [Administration](administration.log) |
| Frontend adapter and error tests | Nine passing checks | [Unit tests](frontend-unit.log) |
| Browser workflows | 16 passing checks | [Browser tests](browser.log) |
| Frontend lint | Passed | [Lint](frontend-lint.log) |
| npm dependency audit | Zero reported vulnerabilities | [Audit](frontend-audit.json) |
| NuGet dependency audit | No reported advisories | [Audit](dependency-audit.log) |
| Aspire integration and Razor fallback | Passed | [Browser checks](aspire-browser.log) |
| Published container | Build, startup, assets, and realtime passed | [Container](container.log) |

Browser coverage includes all 17 workspaces without the Razor assembly.
JWT, Basic, and API-key authentication work with protected APIs and realtime connections.
Basic and API-key language changes refresh English plugin data without reauthentication.
JWT cookies remain HttpOnly. Credentials are absent from browser storage.

Configuration tests cover native fields, rename, activation, deletion, snapshots, comparisons, rollback, import, and export.
Failure injection covers validation, revision conflicts, storage failures, and activation failures.
Each failure preserves the editor draft and displays the appropriate recovery information.
Plugin checks cover bindings, presets, and activation through real administration APIs.
Two-factor setup, login challenges, and disablement use real endpoints.
Assistant checks use a controlled stream and verify explicit write-tool confirmation.
Live connection checks cover pause, reconnect, and expired sessions.

Administration checks verify actual traffic updates through authenticated route groups.
Persistence checks verify disabled routes after rename, restart, import, and rollback.
Two-factor checks cover private file permissions, reload, corruption, write failure, and rejected login.

## Accessibility and visual review

Axe checks use WCAG 2 A/AA and WCAG 2.1 AA rules.
Checked surfaces include English desktop pages, an English mobile dialog, and dark mode.
No violations were reported on these surfaces.
Keyboard checks cover dialog focus, Escape dismissal, and focus return.
Responsive checks cover 390-pixel mobile width and 200% text zoom without page overflow.

Reviewed screenshots:

- [English desktop](desktop-overview.png).
- [English mobile, dark theme](mobile-english.png).
- [Current English workspace](workspace-overview.png).

## Reproduction

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp test
npm --prefix src/Wafish.ReactUi/ClientApp audit
dotnet list Wafish.slnx package --vulnerable --include-transitive
PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
podman build -f samples/SampleGateway/Dockerfile -t localhost/wafish:verification .
```

Browser tests create three disposable hosts with generated credentials and encrypted temporary databases.
The local Chromium override can be omitted after installing Playwright's Chromium.
The container check used direct loopback listeners and explicitly enabled anonymous loopback administration in Development.
It checked rendered pages, deep links, embedded asset headers, realtime updates, and published Razor assets.
The container and Aspire topology were stopped normally after verification.

## Limits

External AI-provider and notification delivery need deployment-specific credentials and endpoints.
Automated accessibility checks do not certify every assistive technology or screen.
No production deployment was performed.
The native SQLCipher blocker existed at this verification point. [SEC-14](../sec14/README.md) subsequently resolves it.
Clean managed dependency audits do not clear that blocker.
