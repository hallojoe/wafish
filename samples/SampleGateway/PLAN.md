# SampleGateway: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add a container smoke check

Status: TODO.

Start the published image with temporary storage and injected credentials.

Acceptance: The non-root container serves readiness and authenticated administration endpoints.

### IMP-02: Add a reproducible local HTTPS recipe

Status: TODO.

Document certificates, trusted proxies, and secure-cookie behavior for a standalone host.

Acceptance: The documented setup passes login and proxy smoke checks.

## New valuable feature

### FEAT-01: Add a blue-green deployment example

Status: TODO.

Demonstrate a health-checked switch between two backend versions using existing configuration APIs.

Acceptance: The walkthrough proves cutover and rollback without losing the original route identity.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
