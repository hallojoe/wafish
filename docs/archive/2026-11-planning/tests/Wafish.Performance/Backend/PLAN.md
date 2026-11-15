# Backend: proposed work

> Historical record, archived 2026-11-15. This document is not an active plan.
> See the [current backlog](../../../../../../PLAN.md). Old statuses describe their original review date.

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Make the listen address configurable

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../../../../../tests/Wafish.Regression/evidence/benchmark-reliability/README.md).

Replace the fixed benchmark port with an explicit harness option.

Acceptance: Parallel isolated runs can select separate addresses.

### IMP-02: Separate payload generation cost

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../../../../../tests/Wafish.Regression/evidence/benchmark-reliability/README.md).

Precompute selected payloads or report generation cost separately.

Acceptance: Reports can distinguish upstream allocation cost from gateway overhead.

## New valuable feature

### FEAT-01: Add paced streaming scenarios

Status: TODO.

Provide chunked and event-stream responses with controlled chunk sizes and intervals.

Acceptance: The harness can measure first-byte latency and buffering without an external service.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../../DOTNET_REVIEW.md).

### IMP-03: Stop work when the client disconnects

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../../../../../tests/Wafish.Regression/evidence/benchmark-reliability/README.md). Priority: P2. Type: measurement distortion.

Evidence: [Program.cs](../../../../../../tests/Wafish.Performance/Backend/Program.cs), echo and delay endpoints.

The echo copy and delay do not receive `RequestAborted`.
Cancelled load can leave backend work running into the next measurement.
Add explicit request bounds and propagate cancellation through both endpoints.

Acceptance: Cancelled requests stop promptly.
A following scenario starts without leftover delay or echo work.

### FEAT-02: Add deterministic transport faults

Status: DONE. Completed: 2026-11-14.
Verification: Release solution build and all eight `--benchmarks` cases pass.
Real HTTP checks cover truncated responses, cancelled body reads, bounds, reset, and opt-in behavior. Priority: P2. Type: benchmark feature.

Evidence: [Program.cs](../../../../../../tests/Wafish.Performance/Backend/Program.cs) supports status failures but no partial-response disconnect.

Add opt-in responses that disconnect after a fixed byte count or stall after sending headers.
Keep controls on loopback and reset fault state between cases.

Acceptance: The runner can reproduce truncated responses and body-read deadlines.
Backend counters distinguish completed, cancelled, and aborted requests.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](../../../../../../tests/Wafish.Performance/Backend/README.md) and [AGENTS.md](../../../../../../tests/Wafish.Performance/Backend/AGENTS.md) when the implemented behavior changes.
