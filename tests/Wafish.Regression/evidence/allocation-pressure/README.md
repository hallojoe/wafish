# Allocation pressure evidence

Completed priority 11 on 2026-11-14. Changes remain uncommitted.
Release build: zero warnings and errors. Main suite: 225 passing cases. Administration: 35 passing cases.
Changed production source coverage: 53/54 executable lines, 98.15%.

| Measurement | Before | After |
|---|---:|---:|
| Charged bytes for a 512-byte cache response | 1,115,136 | 67,096 |
| Rows written for one changed route among 3,000 | 6,004 | 2 |
| WAL bytes for that update | 1,141,272 | 41,232 |
| Focused transaction duration | 81.2 ms | 24.3 ms |

Timings are local observations, not stable performance thresholds.
Cache charges include header and entry allowances. They do not measure process RSS or delayed garbage collection.
Compaction reserves both arrays before allocation. Under pressure, the original capture remains unchanged.
Thirty-two overlapping misses and eight readers verify eviction, generations, and final-reader accounting.
Empty responses and insufficient-copy-budget fallback are covered.

Sixteen concurrent sixteen-MiB responses verify compression streaming and excluded binary handling.
Compressed JSON allocated approximately 233 KiB; excluded binary allocated approximately 87 KiB in the focused run.
Every stream emitted bytes before producer completion. Existing compression code required no changes.
Native codec state is outside the managed-allocation measurement.

Differential snapshot checks mutate every persisted property independently.
They verify stable rename identities, unchanged destinations after cluster replacement, stale versions, and transaction rollback.
Existing fault fixtures now fail the version update, which every transaction must execute.
They no longer depend on unrelated destination rows being rewritten.

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --allocation-pressure
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --configuration
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
```

Coverage combines the main and administration suites. `changed-coverage.py` consumes their merged coverage XML.
The initial WAL measurement required explicitly enabling WAL in its private fixture; that correction preceded the recorded baseline.

Three isolated package consumers and public API contract checks also pass; see `packages.log`.
