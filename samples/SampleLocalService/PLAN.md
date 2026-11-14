# SampleLocalService: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add sample endpoint contract checks

Status: TODO.

Verify ping, echo, instance headers, and trace identity through a gateway.

Acceptance: Both backend instances satisfy the same endpoint contracts.

### IMP-02: Document standalone registration settings

Status: TODO.

Show explicit gateway URL, gRPC URL, credentials, and destination configuration.

Acceptance: A reader can register one standalone instance without editing gateway internals.

## New valuable feature

### FEAT-01: Add an idempotent order example

Status: TODO.

Provide a sample write operation that demonstrates safe repeated requests with an idempotency key.

Acceptance: Repeated keys do not create duplicate sample orders, and conflicting payloads are rejected.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
