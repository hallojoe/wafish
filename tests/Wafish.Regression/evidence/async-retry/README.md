# Async configuration and retry deadlines

Completed: 2026-11-14. Owner: Codex. Implementation remains in the working tree.

Core IMP-01, IMP-02, and IMP-04 are complete.
Retry IMP-01, IMP-02, and FEAT-02 are complete.

Configuration preparation awaits validators and compilation without blocking caller threads.
Cancellation interrupts startup loading, lock waits, and precommit preparation.
Persistence, activation, and audit retain ownership once a storage commit starts.
Generated concurrent sequences verify rename identities, deletion, registration, stale rejection, and stored/active consistency.

Retry uses controlled timers and jitter sources in tests.
Checks cover Retry-After seconds, dates, malformed values, past dates, maximum delay, and total deadlines.
Existing replay checks cover partial responses, streamed bodies, cancellation, and destination exhaustion.
Deadline checks cover request preparation, retry waits, and non-idempotent bypass.

Administration verification exposed a stale namespace guard that skipped controller authorization.
The guard now matches `Wafish.Dashboard`. Existing authentication tests pass again.

## Verification

```bash
dotnet build tests/Wafish.Regression -c Release --no-restore
dotnet build tests/Wafish.Administration.Regression -c Release --no-restore
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --async-policy
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
```

Both builds report zero warnings and errors.
The main runner passes 168 cases. The administration runner passes 24 cases.
The focused selection passes nine groups.
See [main output](regression.txt), [administration output](administration.txt), and [focused output](focused.txt).
Changed production-line coverage is 88.74%, conservatively treating partially covered ranges as uncovered.
See [coverage](coverage.txt) and [recorded lines](changed-lines.json).
Coverage uses merged `dotnet-coverage collect -f xml` output from both runners.
Run `python tests/Wafish.Regression/evidence/async-retry/changed-coverage.py <merged.xml>` to check the threshold.

## Compatibility and limits

Existing concrete mutation signatures remain available. New cancellation overloads extend the public interface.
Custom interface implementations must add these members.
`RefreshConfigAsync` replaces blocking refresh in administration callers. The synchronous compatibility method remains available.
Plugin install, uninstall, and upgrade controller methods now return `Task<IActionResult>`.
HTTP contracts and protobuf wire fields remain unchanged.

Configured retry bindings now have a 120-second total deadline by default.
The maximum upstream Retry-After contribution defaults to 60 seconds.
Existing non-idempotent restrictions remain unchanged.
Deadlines require cooperative downstream cancellation. Started responses abort instead of returning a replacement status.
YARP validators lack cancellation parameters. Cancellation stops waiting, but cannot stop their internal work.
