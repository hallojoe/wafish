# Wafish.Client: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Add startup retry jitter

Status: DONE. Completed: 2026-11-14.
Verification: Client checks, controlled 1.5-second delay, and cancellation during startup wait pass.

Distribute registration retries when many services restart together.

Acceptance: Tests use controlled delays and verify cancellation during retry waits.

### IMP-02: Test heartbeats during shutdown

Status: DONE.

Exercise an in-flight heartbeat when unregistration starts.

Acceptance: Shutdown cannot register the instance again after successful unregistration.

## New valuable feature

### FEAT-01: Support ordered gateway failover

Status: TODO.

Allow a configured list of registration endpoints with clear ownership rules.

Acceptance: Failover preserves instance identity and does not create conflicting registrations.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Replace the asynchronous timer callback

Status: DONE. Priority: P1. Type: lifecycle defect.

Evidence: [GatewayRegistrationHostedService.cs](Services/GatewayRegistrationHostedService.cs), `StartHeartbeatIfEnabled` and `StopAsync`.

The timer callback becomes `async void` after its first asynchronous wait.
Timer disposal cannot await that continuation.
Slow heartbeats can overlap or register an instance after unregistration.
Use one cancellable heartbeat task and await it before unregistering.

Acceptance: Block a heartbeat, start shutdown, then release it.
No registration occurs after unregistration.
This implements the behavior currently covered by proposal IMP-02.

### FEAT-02: Return structured registration outcomes

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [mutation and lifecycle contracts](../../tests/Wafish.Regression/evidence/mutation-contracts/README.md).
Release build, 200 main cases, 35 administration cases, and 23 browser cases pass.
Changed C# coverage is 87.36%. Three isolated package consumers and six compatibility guards pass. Priority: P2. Type: feature.

Evidence: [GatewayAutoRegistrationClient.cs](Services/GatewayAutoRegistrationClient.cs), `RegisterWithGrpcAsync` and `UpdateDestinationsAsync`.

These methods reduce structured gateway responses to Boolean values.
Add result APIs containing status, persisted revision, active revision, and retry classification.
Retain Boolean wrappers where compatibility requires them.
Propagate caller cancellation separately from remote failure.

Acceptance: Clients distinguish validation, credentials, conflicts, storage failures, and activation failures.
Callers can determine whether retrying could repeat an applied operation.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-02, IMP-03 on 2026-11-14. [Verification](../../tests/Wafish.Regression/evidence/review4567/README.md).
