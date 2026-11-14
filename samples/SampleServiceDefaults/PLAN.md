# SampleServiceDefaults: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Test telemetry source registration

Status: TODO.

Verify expected instruments, activity sources, and health endpoint filtering.

Acceptance: Gateway and backend traces remain correlated without health-request noise.

### IMP-02: Document exporter-free behavior

Status: TODO.

Show how hosts behave when no OTLP endpoint is configured.

Acceptance: Samples remain usable without a collector and do not log exporter retry noise.

## New valuable feature

### FEAT-01: Add redacted telemetry diagnostics

Status: TODO.

Expose an opt-in local diagnostic view of enabled exporters and instrumentation.

Acceptance: The view reports configuration state without revealing credentials or payload data.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
