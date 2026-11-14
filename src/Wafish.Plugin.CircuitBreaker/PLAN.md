# Wafish.Plugin.CircuitBreaker: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Introduce controllable time for recovery tests

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [lifetime stress evidence](../../tests/Wafish.Regression/evidence/lifetime-stress/README.md).
Release build, 187 main cases, and 34 administration cases pass. Changed source coverage is 89.69%.

Replace wall-clock assumptions with an injectable time source where needed.

Acceptance: Open and half-open transitions can be tested without real delays.

### IMP-02: Stress concurrent half-open requests

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [lifetime stress evidence](../../tests/Wafish.Regression/evidence/lifetime-stress/README.md).
Release build, 187 main cases, and 34 administration cases pass. Changed source coverage is 89.69%.

Run competing probes against one destination and simulate cancellation.

Acceptance: The configured probe limit is never exceeded.

## New valuable feature

### FEAT-01: Add configurable recovery jitter

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: Release regression build and `--group need-six`; deterministic jitter and probe-limit checks pass.

Spread recovery probes across replicas after a shared outage.

Acceptance: Probe times vary within configured bounds without extending outages indefinitely.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Acquire probe permits only for selected destinations

Status: DONE. Priority: P1. Type: concurrency risk.

Evidence: [CircuitBreakerMiddleware.cs](Middleware/CircuitBreakerMiddleware.cs).
See [DestinationCandidateCoordinator.cs](../Wafish.Runtime/Infrastructure/Resilience/DestinationCandidateCoordinator.cs).

Candidate enumeration calls `TryAcquire` for every destination.
That operation consumes a half-open permit before destination selection.
Retry middleware can enumerate candidates again.
Separate eligibility checks from probe acquisition and release unused permits.

Acceptance: With two recovering destinations, selecting one consumes only its permit.
Cancellation releases reservations.
Each forwarding attempt records one outcome.

### FEAT-02: Add destination maintenance windows

Status: TODO. Priority: P2. Type: feature.

Evidence: [CircuitBreakerController.cs](Controllers/CircuitBreakerController.cs) currently exposes status and global reset.

Add an authenticated maintenance operation for one destination.
Record actor, reason, expiry, and whether recovery probes are allowed.
Keep maintenance state separate from failure statistics.

Acceptance: Only the selected destination stops receiving new traffic.
Expiry or explicit cancellation restores normal eligibility.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-03 on 2026-11-14. [Verification](../../tests/Wafish.Regression/evidence/review4567/README.md).
