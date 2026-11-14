# Review items 1–3 verification

Completed: 2026-11-14. Owner: Codex.
Base commit: `bc1b617694ccccfe8d8c78b2c3661f15f3b08255`.
Implementation and verification are uncommitted working-tree changes. No completion commit was created.

## Results

- Release solution build passed for all 30 projects.
- Main regression runner: 110 checks passed.
- Administration regression runner: 24 checks passed.
- Changed production source-line coverage: 274 of 299 measured lines, or 91.64%.
- `git diff --check` passed.

The build retains one existing CS0108 warning in `RegistrationClientChecks.CaptureGrpcClient.Heartbeat`.
Local Sonar integration also reports missing analysis targets. This did not fail the build.

See [build output](build.log), [regression output](regressions.log), and [coverage summary](changed-coverage.txt).
Coverage counts added or modified production source lines intersecting .NET coverage ranges.
The collector combines both regression executables. A line counts as covered when a recorded range covers it.
The raw coverage XML is generated locally and is not checked in.

## Reproduction

Before fixes, focused checks reproduced three failures:

- Invalid CIDR prefixes passed schema validation.
- Compressed request log capture expanded beyond its decoded byte limit.
- Concurrent limiter admission exceeded 2,000 partitions.

The new tests were also run with the original retry, WAF logging, and authentication implementations restored temporarily.
They produced seven main-runner failures and two administration failures.
Failures covered unsafe replay, late header changes, delayed streaming, secret-bearing logs, and missing attempt limits.
The implementation files were restored before final verification.

## Commands

Run from the repository root:

```bash
dotnet build Wafish.slnx -c Release
dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish-review-coverage.xml tests/Wafish.Regression/evidence/review123/run-checks.sh
python tests/Wafish.Regression/evidence/review123/changed-coverage.py /tmp/wafish-review-coverage.xml
git diff --check
```

The coverage script runs these built executables:

```bash
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
```

## Coverage and compatibility

Checks include IPv4/IPv6 boundary prefixes, activation validation, and seeded secrets in WAF rejection logs.
Authentication checks cover concurrent admission, expiry, source isolation, account limits, capacity, Basic, API keys, and TOTP attempts.
HTTPS uses a real cookie container and a pinned temporary certificate.
Cookie checks cover login, logout, token expiry, HTTP exclusion, host isolation, and origin rejection.

Replay checks cover complete, unknown-length, oversized, truncated, and cancelled request bodies.
Live Kestrel checks cover response start, flush, completion, overflow, synchronous writes, body writers, and file transfer.
Two live servers verify YARP request replay.
Combined retry, cache, and compression checks verify early bytes, complete bodies, cache reuse, and disconnect cleanup.
Limiter checks cover concurrent admission, stable route partitions, queue cancellation, active leases, cleanup, and shutdown ownership.
Logging checks cover gzip, Brotli, deflate, malformed input, UTF-8 boundaries, cancellation, and caller stream ownership.

See [security guidance](../../../../docs/security.md#consumer-changes-for-review-fixes) for consumer migrations and limits.
The store contract adds `AcquireAsync`. Direct controller construction requires the shared authentication limiter.
Unknown-length and oversized request bodies no longer retry. Sent response bytes end retry eligibility.
Existing JSON envelopes remain unchanged, except that authentication exhaustion now returns HTTP 429.

This verification ran locally on Linux. It does not claim new cross-platform or deployment-specific integration results.
NET-IMP-03 remains open for queued-record accounting and wider Client/Runtime shutdown work outside approved items 1–3.
