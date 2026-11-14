# Review items 11–13 verification

Completed: 2026-11-14. Owner: Codex.
Base commit: `bc1b617694ccccfe8d8c78b2c3661f15f3b08255`.
Changes remain uncommitted. Existing changes for items 1–10 were preserved.

## Results

- Release solution build: zero warnings and errors.
- Main regression runner: 134 checks passed.
- Administration regression runner: 24 checks passed.
- Storage-only runner: five checks passed.
- Total: 163 .NET regression executions, including six new focused checks.
- Browser runner: 20 checks passed, including three prefixed authentication modes.
- Frontend unit tests: 27 checks passed. Lint passed.
- Asset validator: 13 checks passed with 100% line, branch, and function coverage.
- Isolated frontend build and pack checks: five checks passed.
- React UI package and symbol package created successfully.
- Changed C# production-line coverage: 61/65, or 93.85%.
- Local documentation links and `git diff --check` passed.

See [build](build.log), [regressions](regressions.log), [browser](browser.log), and [changed coverage](changed-coverage.txt).
See [asset validation](asset-validation.log), [build inputs](build-inputs.log), [frontend](frontend.log), [lint](lint.log), and [package](package.log).
Missing local Sonar targets produced informational messages without compiler warnings.

## Verified behavior

The shell includes PathBase and nested UI/API prefixes in injected URLs.
Live HTTP checks retrieve embedded assets under root and prefixed paths.
Browser checks cover JWT, BasicAuth, and API-key authentication under `/edge/manage/ui`.
Deep links, navigation, APIs, and realtime connections remain within `/edge/`.

The manager retains retirement tasks and joins current and retired domains during shutdown.
Four leased generations remain alive until their leases finish.
Repeated shutdown shares completion. Late requests, preparation, and commits are rejected.
Rejected candidates and failed pipeline construction release owned providers.
Disposal errors remain visible across repeated shutdown calls.
The post-transition watcher is cancelled and joined.

Cache schema descriptions deprecate the ineffective variant switches without reducing isolation.
Tests preserve host, query, header, and credential boundaries under both legacy setting combinations.
Compression tests cover thresholds below, at, and above 64 KiB.
Known and unknown lengths, short and large responses, and single versus fragmented writes retain exact content.
Unknown lengths above the prefix budget consistently bypass compression.

The asset validator rejects missing entry files, CSS, fonts, chunks, imports, and HTML references.
It also rejects invalid manifest entries, missing configuration markers, and invalid asset paths.
The isolated MSBuild fixture uses the actual project targets with a small local asset generator.
No-change builds skip generation. Package-script changes and added/removed inputs cause generation.
Packing rejects a missing referenced asset with a clear diagnostic.

## Additional defects found during verification

An immediate notification stop can cancel BackgroundService before ExecuteAsync starts.
The existing accounting regression exposed a queue left behind in that case.
Stop now discards queued events after the worker completes, including cancellation before startup.
The full accounting suite passes with this correction.

The repository contains both `logo.png` and `Logo.png`.
NuGet source resolution matched both names and reported duplicate package-icon destinations.
The React UI pack target stages its selected icon in a dedicated intermediate directory.
Both the regular package and symbol package then build successfully.
Other package consumers and broader compatibility remain item 14.

## Reproduction

Run from the repository root:

```bash
dotnet build Wafish.slnx -c Release
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --review-deployment
dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish111213-coverage.xml tests/Wafish.Regression/evidence/review111213/run-checks.sh
python3 tests/Wafish.Regression/evidence/review111213/changed-coverage.py /tmp/wafish111213-coverage.xml
npm --prefix src/Wafish.ReactUi/ClientApp run lint
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
node --test --experimental-test-coverage src/Wafish.ReactUi/ClientApp/scripts/verify-assets.test.mjs
python3 tests/Wafish.Regression/evidence/review111213/check-frontend-build.py
UI_TEST_NO_BUILD=true PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
dotnet pack src/Wafish.ReactUi -c Release --no-build -o /tmp/wafish111213-packages
git diff --check
```

Use an installed Chromium executable or omit its override when Playwright browsers are installed.
Coverage measures production C# lines changed after items 1–10.
[changed-lines.json](changed-lines.json) preserves that baseline comparison.
The asset validator has separate Node coverage. Frontend application code was not changed.
Raw coverage XML, package artifacts, and diagnostic binary logs remain local.

## Limits and compatibility

See [consumer guidance](../../../../docs/security.md#consumer-changes-for-review-items-1113).
Long requests can delay domain shutdown. Providers are not forcibly disposed while leases remain active.
Cache switches remain accepted but deprecated. Compression fallback above 64 KiB is now independent of chunk size.
Node.js is required to validate assets, including when frontend generation is skipped.
Tests used Linux, local HTTP servers, and Chromium. HTTPS hosting and other operating systems were not retested.
The wider package-consumer, dependency-audit, clock, and retention proposals remain separate tasks.
