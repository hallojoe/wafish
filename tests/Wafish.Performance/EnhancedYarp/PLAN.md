# EnhancedYarp: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Validate mode fidelity at startup

Status: DONE.

Check that every advertised feature mode creates the intended effective bindings and resources.

Acceptance: Each mode reports its effective configuration and fails if required features are inactive.

### IMP-02: Remove remaining legacy labels and configuration keys

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../Wafish.Regression/evidence/benchmark-reliability/README.md).

Review scenario names and options against current plugin manifests.

Acceptance: The harness uses accepted schema fields and clearly named comparison groups.

## New valuable feature

### FEAT-01: Add a bounded plugin-combination matrix

Status: TODO.

Generate selected plugin combinations with a recorded configuration for each run.

Acceptance: Reports attribute cost to explicit combinations and keep the matrix size controlled.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../../DOTNET_REVIEW.md).

### Evidence for IMP-01 and IMP-02: Verify effective feature modes

Priority: P1. Type: benchmark fidelity gap.

Evidence: [Program.cs](Program.cs), mode configuration and binding creation.

Only retry and circuit modes create explicit plugin bindings.
WAF and rate-limit labels do not create their required bindings.
Retry configuration writes `statusCodes`, but the manifest defines `retryOnStatusCodes`.
The health endpoint becomes available before binding setup completes.

Acceptance: Each advertised mode installs validated bindings and reports its effective plan.
Readiness waits for those bindings.
A WAF probe is blocked, and a rate-limit probe exhausts permits.

### FEAT-02: Export a benchmark configuration manifest

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../Wafish.Regression/evidence/benchmark-reliability/README.md). Priority: P2. Type: benchmark feature.

Evidence: [Program.cs](Program.cs) selects configuration from mode strings.

Export the effective route, plugin versions, bindings, and resource states for each run.
Exclude credentials and connection secrets.
Attach a configuration hash to the runner report.

Acceptance: A report can prove which plugins processed its workload.
Identical manifests produce comparable host compositions.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-01 on 2026-11-14. [Verification](../../Wafish.Regression/evidence/review4567/README.md).
