# Review items 4–7 verification

Completed: 2026-11-14. Owner: Codex.
Changes remain uncommitted. Existing changes for items 1–3 were preserved.

## Results

- Release solution build passed with zero compiler warnings and errors.
- Main regression runner: 118 checks passed.
- Administration regression runner: 24 checks passed.
- Storage-only runner: five checks passed.
- Live Redis runner: four checks passed.
- Managed performance suite: 23 scenarios passed.
- A stalled response body caused exit code 1 after 10.21 seconds.
- A deliberate zero-sample managed baseline returned exit code 1.
- Changed production-line coverage: 279/311, or 89.71%.
- `git diff --check` passed.

Local Sonar integration reports missing analysis targets. These messages did not fail compilation.
Performance runs verify behavior on this Linux host. They do not establish production capacity.

See [build output](build.log), [regressions](regressions.log), [coverage](changed-coverage.txt), and [managed results](performance.csv).
Deadline evidence appears in [stalled-runner.log](stalled-runner.log).
The deliberate failing managed run appears in [managed-failure.log](managed-failure.log).

## Verified behavior

Benchmark modes install explicit bindings and validate effective plans before readiness.
Health reports the selected mode and active policies.
WAF blocks the attack probe without upstream traffic.
Retry reaches the backend three times before success.
Circuit opening limits failed upstream attempts, then permits recovery.
Rate-limit reports distinguish expected HTTP 429 responses from transport failures.

A deterministic storage barrier holds a route mutation while binding and discovery writers wait.
All successful changes remain in the final snapshot.
Unrelated publications preserve discovered destinations.
Removed discovery bindings reject captured refresh results.
Endpoint insertion preserves surviving destination IDs.

Atomic binding tests cover stale revisions, direct writes, partial-write rollback, and administration batches.
Injected activation failures exercise compensation and explicit reconciliation.
Compensation cannot overwrite a newer successful storage revision.
Configuration activation can recover without restarting.

Live forwarding reserves only the selected destination probe.
Each retry records one circuit outcome.
Canceled probes release ownership. Late outcomes cannot close a newer circuit generation.
Limiter tests cover admission, queued cancellation, active leases, cleanup, and shutdown disposal.
Redis tests cover shared counters, policy changes, outages, and reconnection.

A blocked third heartbeat cannot re-register after shutdown begins.
Shutdown joins the loop before unregistering.
Destination updates preserve peer instances and reject stale, unknown-instance, invalid, and legacy requests.

## Reproduction

Run from the repository root:

```bash
dotnet build Wafish.slnx -c Release
dotnet build tests/Wafish.Performance/EnhancedYarp -c Release
dotnet build tests/Wafish.Performance/Runner -c Release
export WAFISH_REDIS_SERVER=/absolute/path/to/redis-server
dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish4567-coverage.xml tests/Wafish.Regression/evidence/review4567/run-checks.sh
python3 tests/Wafish.Regression/evidence/review4567/changed-coverage.py /tmp/wafish4567-coverage.xml
dotnet tests/Wafish.Performance/Runner/bin/Release/net10.0/Runner.dll --suite first --duration 1 --warmup 0 --soak-duration 1
python3 tests/Wafish.Regression/evidence/review4567/check-stalled-runner.py
git diff --check
```

The negative managed check intentionally returns 1:

```bash
dotnet tests/Wafish.Performance/Runner/bin/Release/net10.0/Runner.dll --duration 0 --warmup 0 --concurrency 1 --repetitions 1
```

Coverage includes changed production source lines for items 4–7 only.
The baseline was the working tree after items 1–3, captured before this implementation.
[changed-lines.json](changed-lines.json) records those source-line changes.
Coverage excludes tests, generated code, and benchmark executables.
The raw XML remains local.

## Consumer changes

See [security and migration guidance](../../../../docs/security.md#consumer-changes-for-review-items-47).

- Storage providers must support atomic binding snapshots and revision-checked batches.
- Limiter integrations must use disposable acquisition leases.
- Circuit stores must implement the owned attempt contract.
- Redis keys use a new policy namespace. Coordinate replica upgrades.
- gRPC destination updates require instance identity and an expected configuration revision.
- Instance identity remains within the existing trusted registration boundary.

Configuration reconciliation uses `DynamicYarpConfigService.ReconcileAsync`.
Binding reconciliation uses `IPluginBindingMutationService.ReconcileAsync`.
Both operations rebuild stored state under the shared publication gate.

Remaining review proposals retain their existing scope and status.
