# Storage recovery, query bounds, and backup verification

Completed: 2026-11-14. Owner: Codex. Implementation remains in the working tree.

Completed SQLite IMP-01, IMP-02, FEAT-01, and FEAT-02.
Completed Storage.Abstractions IMP-01 and IMP-02.
Offline inspection was completed with restoration verification because both use the same private-copy workflow.

Provider-neutral checks cover rollback, null values, identity preservation, stale writers, cancellation, ordering, and paging.
Fault checks cover migration body failures, ledger failures, cancellation, and forced process termination with committed WAL data.
Recovery preserves completed migrations and replays only the unfinished step.

Read limits clamp to 1–500. Log filters reject values above 1,024 characters. Page offsets use 64-bit arithmetic.
Stable timestamp ties have deterministic secondary ordering. Log counts and rows share a read transaction.
The 50,000-row stress fixture stays below 64 MiB of managed allocations and ten seconds.
Measured allocations and elapsed time are recorded in [regression output](regression.txt).

The offline tool checks integrity and pending migrations. Restoration verification also migrates and reads a private copy.
Tests verify plaintext and encrypted backups, incorrect keys, truncation, foreign databases, future schemas, and invalid JSON.
Source hashes remain unchanged. Command output contains no key or configuration values.

## Verification

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --storage-reliability
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
```

The solution build reports zero warnings and errors.
The main suite passes 178 cases. The administration suite passes 33 cases.
The focused storage selection passes ten groups.
Changed production-line coverage is 139/145 lines, or 95.86%, including the offline command.
See [build](build.txt), [main](regression.txt), [administration](administration.txt), and [coverage](coverage.txt).
Coverage uses merged XML from both runners and the recorded changed-line list.

## Compatibility and limits

Zero, negative, and oversized history limits now normalize to 1–500.
The administration log-history endpoint retains its 200-row limit and returns 400 for oversized string filters.
Offset pages are stable only while the dataset is unchanged. Concurrent writes can move rows between later pages.
Performance bounds apply to the regression fixture, not arbitrary data sizes or hardware.

Backup verification requires a closed, standalone file without WAL or rollback-journal companions.
The source is read-only to this workflow. All migrations and restoration checks use a private copy.
The tool checks stored references and JSON readability; it does not start a gateway or contact upstream services.
Encryption keys use `WAFISH_BACKUP_PASSWORD`, never command arguments.

Public API snapshots were updated for the additive storage types. Three isolated package consumers pass.
See [package verification](packages.txt).
