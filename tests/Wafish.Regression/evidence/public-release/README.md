# Public release verification

Date: 2026-11-14. Base: `8064b39`. Target: public packages for external adopters.
Scope: R01–R14. R15–R55 remain deferred. No packages were published and no commits were pushed.

## Implemented scope

- R01 reconciles existing resource-limit and cancellation evidence.
- R02 masks configured route headers and JSON paths before persistence and realtime delivery.
- R03 enforces atomic logical storage quotas and reports body and metadata discards.
- R04 provides the independently runnable MinimalGateway sample.
- R05–R06 validate ordering constraints and host capability versions before candidate service creation.
- R07 adds optional registration capability discovery with explicit legacy fallback.
- R08 exposes deterministic embedded asset identity and administration compatibility metadata.
- R09 adds bounded startup retry jitter and cancellable waits.
- R10 limits extra attempts across routes sharing a cluster. Initial attempts remain unaffected.
- R11–R13 define metric units, statistic descriptors, and bounded OpenTelemetry instruments.
- R14 adds opt-in loopback truncation and stalled-body fixtures with distinct outcome counters.

## Verification

- Release solution build: zero warnings and errors.
- Main executable regression suite: 242 cases passed.
- Administration regression suite: 35 cases passed.
- Frontend lint passed. Frontend unit tests: 81 passed.
- Browser checks: 30 passed, including authentication, mutation outcomes, accessibility, and session lifecycle.
- Minimal sample HTTP smoke: readiness, backend forwarding, and absent administration endpoints passed.
- Public API comparison found no removed declarations against the previous baselines.
- Three isolated packed consumers passed after the final correction: runtime, administration, and React UI.
- Final retry review preserves the original upstream exception when the shared allowance is exhausted.
- Focused retry-budget and streaming checks passed after that correction.
- Changed executable-line coverage: 305/315, or 96.83%.

Coverage combines the full regression run, minimal-host smoke, and final retry-budget run.
See [coverage totals](changed-line-coverage.json).
The full suite ran before the final exception-preservation correction; affected retry suites ran after it.

Commands:

```bash
dotnet build Wafish.slnx -c Release
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --retry-budget
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --retry-streaming
python3 scripts/verify-packages.py
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

## Limits

Verification ran on Linux. Windows and macOS runtime checks were not executed.
Log quotas charge retained fields and row allowances. They do not impose a physical filesystem limit.
Retry budgets are host-local. Metric counters reset with their documented store and identity lifetimes.
Administration trusts authenticated administrators. Operation permissions remain deferred.
See [consumer changes and publication policy](../../../../docs/public-release.md).
