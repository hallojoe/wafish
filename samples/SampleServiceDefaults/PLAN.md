# SampleServiceDefaults: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test telemetry source registration

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample contracts](../../tests/Wafish.Regression/evidence/sample-contracts/README.md).

Verify expected instruments, activity sources, and health endpoint filtering.

Acceptance: Gateway and backend traces remain correlated without health-request noise.

### IMP-02: Document exporter-free behavior

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample contracts](../../tests/Wafish.Regression/evidence/sample-contracts/README.md).

Show how hosts behave when no OTLP endpoint is configured.

Acceptance: Samples remain usable without a collector and do not log exporter retry noise.

## New valuable feature

### FEAT-01: Add redacted telemetry diagnostics

Status: TODO.

Expose an opt-in local diagnostic view of enabled exporters and instrumentation.

Acceptance: The view reports configuration state without revealing credentials or payload data.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Filter all configured health probes from traces

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample contracts](../../tests/Wafish.Regression/evidence/sample-contracts/README.md). Priority: P2. Type: telemetry mismatch.

Evidence: [Extensions.cs](Extensions.cs), `ConfigureOpenTelemetry`.
The [gateway host](../SampleGateway/Program.cs) also exposes `/ready` and `/live`.

The trace filter excludes only `/health` and `/alive`.
Gateway readiness and liveness probes therefore remain in tracing.
Read effective probe paths or share their definitions with the gateway host.

Acceptance: Every configured probe path is excluded.
Normal API requests retain correlated traces.

### FEAT-02: Add optional registration readiness

Status: TODO. Priority: P2. Type: sample feature.

Evidence: [Extensions.cs](Extensions.cs), `AddDefaultHealthChecks`, registers only a successful self check.

Allow backend samples to require successful gateway registration for readiness.
Keep liveness independent of gateway availability.
Use a Client registration-status contract rather than issuing registration calls from health probes.

Acceptance: A running unregistered backend stays live but reports not ready when the option is enabled.
Successful registration changes readiness without restarting the backend.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
