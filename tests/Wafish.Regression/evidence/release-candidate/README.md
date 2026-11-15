# Release candidate verification

Candidate: `1.0.0-rc.1`. Prepared: 2026-11-15.
Source preparation starts from `963bb204fb7024de30315220e0ec210414662fcd`.
Initial advertised support: Linux x64 with .NET 10.
Public API snapshots remain unchanged. No package or release tag is published.

## Hosted defects repaired

[Prior Verify run](https://github.com/hallojoe/wafish/actions/runs/43655673543) failed in five test cases.
Three lifetime cases could not find the Release collectible plugin fixture.
The solution built that referenced fixture as Debug because it was absent from the solution configuration.
Adding the fixture to the solution makes its configuration follow the solution build.

Two Windows storage cases read database headers while a reopened SQLite connection still held the file.
Those cases now dispose the connection and provider before reading the header.
The encryption assertions and test names remain unchanged.

## Candidate checks

Local checks passed:

- Release solution build: zero warnings and errors; fixture output uses Release.
- Main regression suite: 286 cases, including the three previously failing lifetime cases.
- Administration: 44 cases. Native storage: five cases. Live Redis: one suite with four scenarios.
- Browser: 30 cases. Frontend unit tests: 81 cases. Fixture ownership: five cases.
- Frontend lint and npm vulnerability audit passed. No npm vulnerabilities were reported.
- Asset validation: 13 cases. Isolated frontend build and packing: five scenarios.
- Three isolated candidate consumers and API compatibility passed.
- Six API rejection/compatibility fixtures passed. Dependency auditing and negative audit fixtures passed.
- Nineteen candidate package manifests contain the expected version, release notes, README, and icon.
- Documentation links and whitespace checks passed.

See [main results](main-results.json), [administration](admin-results.json), [storage](storage-results.json), and [Redis](redis-results.json).
See [local package hashes](local-packages.json) and [audit result](audit-summary.json).
Hosted verification of the preparation commit is pending.
The local staging feed is `artifacts/package-checks`.
Consumers use fresh caches outside repository imports, with no project references.
They verify runtime, administration, UI assets, native encryption, and incorrect-key rejection.
Local package hashes are recorded separately from hosted artifact hashes.

## Reproduction

```bash
dotnet build Wafish.slnx -c Release
WAFISH_TEST_REPORT=artifacts/test-results/main.json dotnet run --project tests/Wafish.Regression -c Release --no-build
WAFISH_TEST_REPORT=artifacts/test-results/administration.json dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
WAFISH_TEST_REPORT=artifacts/test-results/storage.json dotnet run --project tests/Wafish.Regression -c Release --no-build -- --storage
WAFISH_REDIS_SERVER=/path/to/redis-server dotnet run --project tests/Wafish.Regression -c Release --no-build -- --redis
python3 scripts/verify-packages.py
python3 tests/Wafish.PackageChecks/check-negative.py
python3 scripts/test-audit.py
python3 scripts/audit-dependencies.py
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
npm --prefix src/Wafish.ReactUi/ClientApp audit
npm --prefix src/Wafish.ReactUi/ClientApp run test:fixtures
UI_TEST_NO_BUILD=true npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

## Benchmark disposition

Retain the existing external run as exploratory evidence.
See [review and limitations](../../../Wafish.Performance/results/external-20261115-review.md).
It lacks a configuration hash, comparison run, process measurements, and repeated samples.
It does not establish a release performance budget or production capacity.

## Publication boundary

The preparation branch is used for hosted verification.
Publication and a release tag require approval of the concrete candidate and its remaining limitations.
