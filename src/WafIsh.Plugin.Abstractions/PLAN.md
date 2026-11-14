# WafIsh.Plugin.Abstractions: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add contract compatibility checks

Status: TODO.

Check public interface and record changes during package updates.

Acceptance: Unexpected binary or source breaks fail a dedicated compatibility check.

### IMP-02: Clarify lifecycle failure contracts

Status: TODO.

Specify cancellation, repeat calls, and partial startup behavior for resources.

Acceptance: Reference implementations and tests demonstrate each lifecycle outcome.

## New valuable feature

### FEAT-01: Declare host capability requirements

Status: TODO.

Allow manifests to state required host capabilities and their versions.

Acceptance: The host rejects unsupported plugins before creating their resources.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
