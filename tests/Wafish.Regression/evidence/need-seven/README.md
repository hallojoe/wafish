# Need-7 features: R19–R28

Completed on 2026-11-14. Base commit: `d4f1bd9`.

- R19: persistent instance leases, credential ownership, renewal grace, expiry, and graceful draining.
- R20: local client registration status and optional sample readiness.
- R21: bounded latency distributions and OpenTelemetry histogram instruments.
- R22: authenticated cache purge by route and tag, with pending-fill fencing.
- R23: explicit detection-only WAF decisions with mandatory inspection limits.
- R24: exact parsed-field exclusions, configuration previews, and exclusion audits.
- R25: plugin generation, active-request, retirement-age, and disposal-failure diagnostics.
- R26: provider capability metadata and startup requirements.
- R27: provider-owned consistent backups with encryption and temporary-resource ownership.
- R28: bounded, redacted, authenticated diagnostic bundle downloads.

Implementation commits: `a6fdf48`, `57aecbc`, `2fa08a5`, and `3124423`.

## Verification

- Release solution build: zero warnings and errors.
- Main suite: 264 passed in the final full run.
- Final focused feature run: 13 passed, including one additional shutdown-deadline case.
- Unique main cases verified: 265.
- Administration suite: 42 passed.
- Browser suite: 30 passed.
- Three isolated package consumers passed: runtime, administration, and React UI.
- Public API verification passed. Every previous public declaration remains present.
- Changed production executable-line coverage: 522/551, or 94.74%.

See [case results](checks.json) and [coverage](changed-line-coverage.json).
Coverage combines the final main suite, final focused suite, and administration controller coverage.
Only administration source files use the administration coverage contribution.
This conservative merge avoids combining earlier core source-line positions after the final ownership guards.
Production source remained unchanged between the final full and focused runs.
The final focused run adds explicit coverage for the bounded client shutdown wait.

The first full run found an outdated cache allocation boundary in a regression fixture.
Tags increased the reserved metadata allowance from 1 KiB to 4 KiB.
The fixture now tests the new saturation boundary. The final full run passes.

```bash
dotnet build Wafish.slnx -c Release
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --group need-seven
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
python3 scripts/verify-packages.py --update-api
python3 scripts/verify-packages.py --skip-pack
```

The second package check reused freshly packed artifacts after updating the additive API snapshots.
No package was published. No commit was pushed.
Verification ran on Linux.

## Acceptance evidence

Controlled clocks verify renewal, grace, expiry, healthy peers, legacy peers, and stale-token rejection.
Restart checks preserve lease metadata and draining state.
A separate shutdown check verifies the configured drain wait before unregistering.
Sample checks verify initial unready state, success, transient loss, expiry, recovery, drain, and independent liveness.

Held plugin requests remain visible through replacement and disappear after disposal.
Disposal failures retain safe records without exception text.
Provider timeouts produce partial diagnostic bundles. A held probe blocks new probe collections until it completes.
Known provider and authentication secrets do not appear in exports.

Known latency samples verify exact boundaries, overflow, concurrent counts, and second-based telemetry.
Cache checks verify route and tag isolation and prevent a pending fill from restoring purged content.
WAF checks verify detection reports, unchanged forwarding, mandatory limits, and exact field exclusions.
HTTP checks cover exclusion previews and audits for individual and batch mutations.
Registered mutation services also audit trusted in-process exclusion changes.

The administration suite verifies encrypted backups with committed WAL data and rejects missing keys.
The route authorization matrix covers all new controller actions.
Reader permissions reject cache purge and retain access to safe diagnostic operations.

See [contracts and limits](../../../../docs/registration-operations-contracts.md).
R29–R55 remain deferred: 27 tasks. All need-7 tasks are complete.
