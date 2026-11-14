# Backend: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Make the listen address configurable

Status: TODO.

Replace the fixed benchmark port with an explicit harness option.

Acceptance: Parallel isolated runs can select separate addresses.

### IMP-02: Separate payload generation cost

Status: TODO.

Precompute selected payloads or report generation cost separately.

Acceptance: Reports can distinguish upstream allocation cost from gateway overhead.

## New valuable feature

### FEAT-01: Add paced streaming scenarios

Status: TODO.

Provide chunked and event-stream responses with controlled chunk sizes and intervals.

Acceptance: The harness can measure first-byte latency and buffering without an external service.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
