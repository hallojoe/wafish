# WafIsh.Grpc: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add wire compatibility fixtures

Status: TODO.

Store representative encoded messages and verify decoding across protocol revisions.

Acceptance: Additive fields preserve old message behavior.

### IMP-02: Define protocol error mappings

Status: TODO.

Document how configuration outcomes map to response fields and gRPC status codes.

Acceptance: Client and server tests agree on each failure outcome.

## New valuable feature

### FEAT-01: Add a capability handshake

Status: TODO.

Let clients query protocol version and supported registration capabilities.

Acceptance: Older clients continue to register when the new RPC is unavailable.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
