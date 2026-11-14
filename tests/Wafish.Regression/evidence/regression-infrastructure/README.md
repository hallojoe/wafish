# Regression infrastructure and workspace states

Implementation date: 2026-11-14. Base commit: `bc1b617`. Changes remain uncommitted.

## Scope

- One shared registry retains the original 200 default cases and their order.
- Dry JSON listing, exact case/group selection, legacy flags, and explicit exit codes are supported.
- Reports include each execution, duration, exception, group, and seeded schedule trace.
- Four fault families use named barriers: configuration, retry, log drain, and initial audit loading.
- Seeds 17, 91, and 2026 run by default. Two seed-91 runs produce identical operation traces.
- Existing controlled plugin replacement and lifecycle checks remain in the default suite.
- Browser fixtures share lifecycle setup and isolate authentication modes, certificates, databases, and directories.
- Profiles cover baseline, 251 routes, representative bindings, and four real mutation failures.
- Fault profiles activate after startup and expose no HTTP control endpoints.
- Components cover all 18 workspaces. Tests cover failures, recovery, stale results, cancellation, and draft ownership.
- Browser checks cover keyboard focus, validation errors, realtime disconnection, mobile navigation, and axe accessibility.

## Defects reproduced and fixed

1. Audit loading could exceed the 200-entry limit and place old entries after recent writes.
   The controlled load test failed before the fix. Merge and eviction now preserve chronological order under one lock.
2. Overview events wrote a query key without the page's locale.
   The displayed-value test failed before the fix. Realtime events now update the displayed query.
3. Assistant session and message queries lacked loading and error states.
   Component checks now verify both states, retry controls, and stale-session cancellation.

## Verification

Run from the repository root:

```bash
dotnet build Wafish.slnx -c Release
WAFISH_TEST_REPORT=/tmp/wafish-main.json dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish-main.xml 'dotnet run --project tests/Wafish.Regression -c Release --no-build'
WAFISH_TEST_REPORT=/tmp/wafish-admin.json dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish-admin.xml 'dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build'
dotnet tool run dotnet-coverage merge /tmp/wafish-main.xml /tmp/wafish-admin.xml -f xml -o /tmp/wafish-coverage.xml
python tests/Wafish.Regression/evidence/regression-infrastructure/changed-coverage.py /tmp/wafish-coverage.xml
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
python tests/Wafish.Regression/evidence/regression-infrastructure/ui-changed-coverage.py
UI_TEST_NO_BUILD=true npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
npm --prefix src/Wafish.ReactUi/ClientApp run test:fixtures
python scripts/verify-packages.py
```

Release build: zero warnings and errors.
Main suite: 209 passing cases. Administration suite: 35 passing cases.
Frontend unit suite: 81 passing tests. Parent cleanup suite: five passing tests.
Changed C# executable lines: 187/214, or 87.38%.
Changed frontend executable lines: 2/2, or 100%; JSX-only wrappers are covered by state assertions.
Coverage comparisons exclude whitespace-only changes. C# measurement includes the shared runner and browser fixture source.
All page modules now have component coverage; whole-frontend line coverage is 51.23%.
This is not a claim of 80% coverage for all pre-existing frontend code.

Browser suite: 30 passing tests across 15 isolated processes.
Public API verification and all three isolated package consumers pass.

## Limits and compatibility

Registry names and existing focused flags remain compatible. Invalid selections now fail explicitly with exit code 2.
JSON reports add schema version, group, and schedule fields. No public production API changes were required.
Fixture options, decorators, and fault profiles are internal to the test host.
The explicit delayed-failure profile waits 1.5 seconds; concurrency schedules use barriers rather than delay-based ordering.
No tests contact a business backend. All seeded destinations use an inert loopback address.
The parent runner still owns cleanup after failed launches and handled interruption.
