# WafIsh.Runtime: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Stress domain replacement

Status: TODO.

Exercise repeated plugin replacement while requests and resource shutdown overlap.

Acceptance: No request uses a disposed provider or loses its active domain.

### IMP-02: Verify collectible plugin cleanup

Status: TODO.

Add repeatable checks for assembly contexts and resources retained after unloading.

Acceptance: Repeated unload cycles release their resources within a defined bound.

## New valuable feature

### FEAT-01: Expose plugin activation previews

Status: TODO.

Return a diagnostic report for a candidate domain without publishing it.

Acceptance: The report includes health and resource requirements while active traffic remains unchanged.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include implementation and acceptance evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Control rate limiter ownership

Status: DONE. Priority: P1. Type: lifecycle risk.

Partial implementation: bounded admission and owned AcquireAsync leases are complete under review items 1–3.
Factories run once under the admission lock. Cleanup preserves queued and active leases.
Legacy GetOrAdd callers still receive raw entries; broader lifecycle acceptance remains pending.
See [verification evidence](../../tests/WafIsh.Regression/evidence/review123/README.md).

Evidence: [InMemoryRateLimiterStore.cs](Infrastructure/State/InMemoryRateLimiterStore.cs), `GetOrAdd` and `Cleanup`.

The dictionary factory creates disposable limiters.
Competing factories can create unused instances.
Cleanup disposes entries without tracking active acquisitions or queued requests.
Give each limiter one owner and defer disposal until its users finish.

Acceptance: Concurrent first access creates no retained unused limiter.
Cleanup does not dispose a limiter with active users.
Host disposal releases every remaining limiter.

### IMP-04: Track retired plugin domains

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: lifecycle risk.

Verification: [review items 11–13 evidence](../../tests/WafIsh.Regression/evidence/review111213/README.md).
Release build, 163 regression executions, and 20 browser checks pass.
Changed C# source coverage is 93.85%. Asset-validator coverage is 100%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [PluginRuntimeDomain.cs](Infrastructure/Plugin/PluginRuntimeDomain.cs), `CommitAsync` and `DisposeAsync`.

Domain retirement runs without retaining its task.
Manager disposal awaits only the current domain.
Track retired domains and their completion during shutdown.

Acceptance: Repeated transitions finish disposal after their final requests complete.
Long streams remain valid until their request leases finish.

### FEAT-02: Report plugin drain progress

Status: TODO. Priority: P2. Type: feature.

Evidence: [PluginRuntimeDomain.cs](Infrastructure/Plugin/PluginRuntimeDomain.cs) tracks active requests and retirement internally.

Expose domain generation, active request count, retirement age, and disposal failures through a diagnostic contract.
Let administration display stalled transitions without accessing service providers.

Acceptance: A held request appears in the retired domain count.
The domain disappears after successful disposal.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-03 on 2026-11-14. [Verification](../../tests/WafIsh.Regression/evidence/review4567/README.md).
