# Runtime lifetime, limits, and shutdown stress

Completed: 2026-11-14. Owner: Codex. Changes remain uncommitted.

Completed Runtime IMP-01/02, CircuitBreaker IMP-01/02, RateLimit IMP-01/02, Administration IMP-02, and ProxyLog IMP-02.

## Checks and fixes

- Hold requests across 64 runtime replacements. Shutdown preserves each provider until its final request completes.
- Copy and repeatedly dispose a lease. Shared ownership releases each request once.
- Direct domain disposal rejects new leases and waits for existing leases.
- Run 16 collectible loads, including injected stop and disposal failures. Retained handles release every context within 12 collection cycles.
- Clear references held by completed async disposal tasks. Detach plugin-defined exception types from retained failures.
- Stop external resources before disposing provider dependencies. Legacy deactivation and host shutdown share disposal ownership.
- Run 32 waves of 128 competing half-open probes. Each wave admits exactly four probes; cancellation restores capacity.
- Verify native fixed-window, sliding-window, token-bucket, and concurrency queue cancellation and replenishment.
- Verify HTTP 429, JSON retry guidance, and upstream isolation for all four algorithms.
- Allocate 5,998 limiters across three partition cleanup cycles. Admission stays bounded at 2,000 partitions.
- Preserve an active quota throughout churn. Cancel its queued request during shutdown; dispose every limiter once.
- Shut down real overview and traffic WebSockets while 101 notifications remain queued or in flight.
- Check notification accounting and credential exclusion. Existing transaction checks reject success audit records before commit.
- Re-run persistence faults during filling, writing, retry exhaustion, transient recovery, and shutdown draining.

Existing accounting checks verify persisted and dropped log totals after storage faults.
Existing controlled-time checks verify circuit recovery and retention across wall-clock changes.

## Verification

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --lifetime-stress
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
python scripts/verify-packages.py --update-api
```

Release build: zero warnings and errors. Main: 187 passing cases. Administration: 34 passing cases.
Focused lifetime selection: nine passing groups. Changed production-line coverage: 87/97, or 89.69%.
See [build](build.txt), [main](regression.txt), [administration](administration.txt), and [coverage](coverage.txt).
The recorded changed-line list compares source against the pre-priority-6 working tree.

## Compatibility and limits

Copied request leases now share one small ownership allocation. Disposing any copy releases that logical lease once.
Direct domain disposal now waits for active requests. A stalled request can delay shutdown.
External host shutdown supports `IAsyncDisposable`; prefer asynchronous deactivation and disposal.
Legacy synchronous wrappers remain available. Plugin stop or disposal failures now propagate after all cleanup attempts.
Retained failure reports preserve type names and messages, without plugin exception instances or original stack frames.

Limiter retry guidance uses lease metadata when available. Token-bucket fallback uses its replenishment period.
Concurrency rejection uses a one-second retry hint. The hint does not reserve a future permit.
Native limiter replenishment uses its own system clock. Tests poll manual replenishment with a five-second watchdog.
Collection bounds apply to the fixture. A plugin with independently rooted threads or static references can still prevent unloading.

Public API baselines include asynchronous host cleanup. All three isolated package consumers pass.
See [package checks](packages.txt).
