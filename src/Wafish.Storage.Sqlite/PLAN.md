# Wafish.Storage.Sqlite: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test interrupted migrations and recovery

Status: TODO.

Inject failures around migration commit, reopening, and WAL recovery.

Acceptance: Reopening either completes safely or reports a clear migration failure.

### IMP-02: Bound large log queries

Status: TODO.

Measure and limit allocations for large filters and pagination requests.

Acceptance: Stress checks keep memory and execution time within documented limits.

## New valuable feature

### FEAT-01: Add offline migration inspection

Status: TODO.

Provide a command that reports pending migrations without changing the database.

Acceptance: The report identifies schema state and pending steps while database bytes remain unchanged.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Correct recent-error timestamp comparisons

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: query defect.

Verification: [review items 8–10 evidence](../../tests/Wafish.Regression/evidence/review8910/README.md).
Release build and 157 regression executions pass. Changed production-line coverage is 86.67%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [SqliteProxyLogAggregator.cs](SqliteProxyLogAggregator.cs), `GetRecent5xxCountAsync`.
See [timestamp writer](../Wafish.Plugin.ProxyLog/Services/SqliteProxyLogWriter.cs), `MapToMeta`.

Stored timestamps use the round-trip format with a `T` separator.
The query compares them with SQLite `datetime` output containing a space.
Text comparison can include expired records from the same date.
Use a consistent sortable UTC representation and migrate existing values where necessary.
Preserve indexed time-range queries.

Acceptance: A 01:00 error is excluded from a five-minute window ending at 12:00.
Cover midnight, offsets, daylight-saving changes, and migrated rows.

### IMP-04: Reduce full snapshot rewrite cost

Status: TODO. Priority: P2. Type: performance hypothesis.

Evidence: [SqliteConfigurationSnapshotRepository.cs](SqliteConfigurationSnapshotRepository.cs), `SaveAsync`.

Every configuration update deletes and reinserts all routes, clusters, and destinations.
Measure transaction duration and WAL growth for large configurations.
Consider differential writes inside the same expected-version transaction.

Acceptance: A one-route update performs less write work at scale.
Rollback, stable identities, and binding renames retain their existing guarantees.

### FEAT-02: Verify backup restoration

Status: TODO. Priority: P2. Type: feature.

Evidence: [SqliteConnectionFactory.cs](SqliteConnectionFactory.cs) and [encrypted storage fixtures](../../tests/Wafish.Regression/fixtures/storage/README.md).

Add an offline verifier that opens a backup copy and checks integrity, schema, and configuration readability.
Report encryption compatibility without printing keys.

Acceptance: Valid plaintext and encrypted backups pass.
Truncated files and incorrect keys fail without changing the original backup.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
