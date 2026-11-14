# Review items 14–16 verification

Completed: 2026-11-14. Owner: Codex.
Base commit: `bc1b617694ccccfe8d8c78b2c3661f15f3b08255`.
Changes remain uncommitted. Existing changes for items 1–13 were preserved.

## Results

- Release solution build: zero warnings and errors.
- Main regression runner: 140 checks passed.
- Administration runner: 24 checks passed.
- Storage-only runner: five checks passed.
- Live Redis: four scenario checks passed, including concurrent acquisition across two clients.
- JSON reports: 170 passing cases, including one aggregate live-Redis case.
- Browser runner: 20 checks passed.
- Public packages: 19 packages and symbol packages produced with package validation enabled.
- Isolated consumers: runtime, administration, and React UI profiles passed.
- API negative fixtures: public method removal and missing assembly both rejected.
- Dependency audit: direct and transitive packages passed; invalid and unsafe reports were rejected.
- Changed C# production-line coverage: 219/246, or 89.02%.
- Workflow YAML parsing, report validation, documentation links, and `git diff --check` passed.

See [build](build.log), [regressions](regressions.log), [focused checks](focused.log), and [coverage](changed-coverage.txt).
See [packages](packages.log), [API rejection checks](api-negative.log), [Redis](redis.log), and [browser](browser.log).
See [audit](audit.log), [audit rejection checks](audit-negative.log), and [audit report](audit-packages.json).
Named case results: [main](main-results.json), [administration](administration-results.json), [storage](storage-results.json), and [Redis](redis-results.json).

## Package compatibility

Package consistency validation runs during packing.
The shared icon target avoids case-insensitive matching of both repository icon filenames.
Consumers compile outside repository imports, with no project references and a fresh NuGet cache.
Source mapping requires local Wafish packages. Other dependencies restore from NuGet.
Runtime hosting excludes administration and presentation assemblies.
Consumers verify plugin composition, authenticated administration, and embedded React entry assets.
All profiles load SQLite3MC, reopen encrypted databases, and reject an incorrect key.
Reopening preserves the factory-selected SQLCipher URI options.

User decision: treat these as first-release packages and use reviewed API snapshots until publication.
Initial API snapshots define the compatibility contract. No earlier released-package baseline is required.
This does not prove compatibility with an earlier released package.
The API guard detects removed assemblies and removed or changed public/protected declarations.
It permits additive APIs. Negative fixtures prove the rejection paths.
See [baseline policy](../../../contracts/public-api/README.md) before updating snapshots.

## CI and reports

CI builds package consumers and encrypted storage checks on Linux, Windows, and macOS.
Local execution in this record covers Linux. Hosted Windows and macOS runs remain pending.
The Linux job runs Redis algorithm, policy-change, concurrent-caller, outage, reconnection, and cancellation checks.
Each Redis process uses an isolated port and temporary directory.

Dependency auditing explicitly includes transitive packages and every vulnerability severity.
Audit warnings, unavailable sources, invalid reports, vulnerabilities, and command failures fail verification.
The audit job archives reports for 30 days. Package and regression artifacts remain for 14 days.
Regression JSON includes case names, duration, status, and complete exception details.
Existing console entry points and exit-code behavior remain available.
Abrupt process termination cannot guarantee JSON output; CI logs remain available.

## Time, storage, and retention

TimeProvider registrations preserve application overrides.
Cache lifetime, circuit recovery, circuit sampling, and cleanup scheduling use monotonic elapsed time.
Injected timers control client backoff and heartbeat, log batching, drain limits, and discovery refresh.
Controlled-clock tests advance those tested policies without waiting for their configured intervals.
Built-in limiter replenishment, Redis quotas, and request deadlines retain their existing timer ownership.

Migration 021 normalizes valid stored timestamps to invariant UTC round-trip text.
Tests cover equivalent instants with different offsets, seven-digit precision, and invalid legacy text.
A 300-row composite-key WITHOUT ROWID fixture verifies paging, offsetless values, and null preservation.
Focused checks also pass with `TZ=Pacific/Auckland`.
Offsetless legacy timestamps mean UTC. Their original local timezone cannot be inferred.
Earlier migrations remain unchanged. Proxy-log comparisons retain indexed UTC-instant semantics.

Metric tests replace identities across 80 versions while concurrent writers record 4,000 retained requests.
Retired series disappear, late completions cannot recreate them, and active counters remain intact.
Each scope admits at most 10,000 series; excess new identities are ignored until capacity becomes available.
The plugin service provider now exposes service availability across plugin and root scopes.
This preserves constructor selection when clock-aware overloads are present.

## Reproduction

Run from the repository root:

```bash
dotnet build Wafish.slnx -c Release
bash tests/Wafish.Regression/evidence/review141516/run-checks.sh
TZ=Pacific/Auckland dotnet run --project tests/Wafish.Regression -c Release --no-build -- --review-release
ANEIANG_REDIS_SERVER=/path/to/redis-server dotnet run --project tests/Wafish.Regression -c Release --no-build -- --redis
python3 scripts/verify-packages.py
python3 tests/Wafish.PackageChecks/check-negative.py
python3 scripts/test-audit.py
python3 scripts/audit-dependencies.py
dotnet tool restore
dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish141516-coverage.xml 'bash tests/Wafish.Regression/evidence/review141516/run-checks.sh'
python3 tests/Wafish.Regression/evidence/review141516/changed-coverage.py /tmp/wafish141516-coverage.xml
```

The changed-line manifest compares this turn against the preserved items 1–13 workspace state.
From `src/Wafish.ReactUi/ClientApp`, run `UI_TEST_NO_BUILD=true npm run test:e2e` after installing Playwright Chromium.
Optional feature proposals retain their existing status. No benchmark performance claim is made.
