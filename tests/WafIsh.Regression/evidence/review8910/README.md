# Review items 8–10 verification

Completed: 2026-11-14. Owner: Codex.
Base commit: `bc1b617694ccccfe8d8c78b2c3661f15f3b08255`.
Implementation remains uncommitted. Existing changes for items 1–7 were preserved.

## Results

- Release solution build: zero warnings and errors.
- Main regression runner: 128 checks passed.
- Administration regression runner: 24 checks passed.
- Storage-only runner: five checks passed.
- Total: 157 regression executions, including ten new focused checks.
- Changed production-line coverage: 130/150, or 86.67%.
- `git diff --check` passed.

See [build output](build.log), [regressions](regressions.log), and [coverage](changed-coverage.txt).
Missing local Sonar analysis targets produced informational messages without compiler warnings.

## Verified behavior

Interrupted log batches survive dequeue, batch filling, and cancelled writes.
Transient writes recover. Exhausted retries increment failures and discard the complete batch.
Cancelled drains retain task ownership until a later stop joins completion.
Every persistence-enabled submission balances against written, pending, or discarded entries.

Concurrent notification overflow records exactly 1500 discarded events from 2500 submissions into a 1000-event queue.
Shutdown and rejected submissions account for the remaining events.
Delivery, suppression, settings failure, send failure, and active cancellation have separate outcomes.
The settings controller exposes the dispatch snapshot.
HTTP handlers are local substitutes. No external notifications were sent.

UTC range tests cover old and future errors, extreme offsets, midnight, daylight-saving offsets, and retention.
Migration 020 indexes existing timestamp strings without rewriting their original values.
An execution-plan assertion verifies index use.
Batch deletion retains referenced clusters and removes the final orphan.
Injected persistence failure preserves active configuration.

Metrics finalize successful, HTTP-error, exception, cancellation, and aborted attempts once.
A live retry records three route, cluster, and destination attempts before success.
Enabling both metric plugins does not duplicate counts. Cluster-only metrics remain active.

## Original defect reproduction

The pre-item-8 versions of the aggregator and route mutation service were restored temporarily.
The focused runner then failed on the expired-error window and final shared-cluster cleanup.
Both fixed files were restored before the final build and full regression run.
See [original-defects.log](original-defects.log).

## Reproduction

Run from the repository root:

```bash
dotnet build WafIsh.slnx -c Release
dotnet tests/WafIsh.Regression/bin/Release/net10.0/WafIsh.Regression.dll --review-accounting
dotnet tool run dotnet-coverage collect -f xml -o /tmp/wafish8910-coverage.xml tests/WafIsh.Regression/evidence/review8910/run-checks.sh
python3 tests/WafIsh.Regression/evidence/review8910/changed-coverage.py /tmp/wafish8910-coverage.xml
git diff --check
```

Coverage includes production source lines changed for items 8–10 only.
The baseline was the working tree after items 1–7, captured before this implementation.
[changed-lines.json](changed-lines.json) records those changes. Raw coverage XML remains local.
Tests, generated code, and earlier implementation changes are excluded from the coverage denominator.

## Compatibility and limits

See [consumer guidance](../../../../docs/security.md#consumer-changes-for-review-items-810).
Webhook settings add a dispatch snapshot. Metric snapshots add exception and cancellation counters.
Retry measurements count pipeline attempts reaching metrics, including downstream rejections without upstream traffic.
These changes do not modify HTTP response statuses.

Log and notification queues remain in memory. Process termination can lose pending records and counters.
Shutdown deadlines require cooperative storage cancellation. A caller-cancelled stop preserves the worker for later joining.
Counters balance at rest; snapshots can change during active processing.
SQLite interprets offset-free legacy timestamps as UTC and compares timestamps at millisecond precision.
Invalid timestamp text remains outside time-range queries.

Verification used this Linux host and local HTTP servers.
External notification delivery, live Redis, and performance suites were not repeated for this scope.
Broader clock standardization and retired runtime-domain lifecycle work remain separate proposals.
