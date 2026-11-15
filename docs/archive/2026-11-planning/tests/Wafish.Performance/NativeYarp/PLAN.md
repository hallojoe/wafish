# NativeYarp: proposed work

> Historical record, archived 2026-11-15. This document is not an active plan.
> See the [current backlog](../../../../../../PLAN.md). Old statuses describe their original review date.

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Make baseline addresses configurable

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../../../../../tests/Wafish.Regression/evidence/benchmark-reliability/README.md).

Allow the runner to supply listener and upstream addresses.

Acceptance: The same route works on isolated port assignments.

### IMP-02: Record effective baseline configuration

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../../../../../tests/Wafish.Regression/evidence/benchmark-reliability/README.md).

Include proxy version and transport settings in benchmark metadata.

Acceptance: Reports identify the exact baseline configuration used.

## New valuable feature

### FEAT-01: Add selectable HTTP/2 and TLS baselines

Status: TODO.

Measure transport overhead with explicit, matched client and upstream settings.

Acceptance: Reports compare equivalent transport modes and document certificate setup.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../../DOTNET_REVIEW.md).

### IMP-03: Verify the workload route before measurement

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../../../../../tests/Wafish.Regression/evidence/benchmark-reliability/README.md). Priority: P2. Type: benchmark readiness gap.

Evidence: [Program.cs](../../../../../../tests/Wafish.Performance/NativeYarp/Program.cs) returns a successful local `/health` response.

This health response does not verify forwarding to the backend.
Require a harness preflight against the actual workload route.
Keep preflight traffic outside measured intervals.

Acceptance: An unreachable backend prevents a measurement from starting.
The preflight checks response status and expected payload.

### FEAT-02: Add a multiple-destination baseline

Status: TODO. Priority: P3. Type: benchmark feature.

Evidence: [Program.cs](../../../../../../tests/Wafish.Performance/NativeYarp/Program.cs) configures one fixed destination.

Support a bounded configurable destination list and explicit YARP load-balancing policy.
Compare the same destinations and transport settings with EnhancedYarp.

Acceptance: Reports identify destination count and distribution.
The baseline keeps its independence from Wafish packages.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](../../../../../../tests/Wafish.Performance/NativeYarp/README.md) and [AGENTS.md](../../../../../../tests/Wafish.Performance/NativeYarp/AGENTS.md) when the implemented behavior changes.
