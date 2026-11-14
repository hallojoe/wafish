# WafIsh.Runtime: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Stress domain replacement

Status: TODO.

Exercise repeated plugin replacement while requests and resource shutdown overlap.

Acceptance: No request uses a disposed provider or loses its active domain.

### IMP-02: Verify collectible plugin cleanup

Status: TODO.

Add repeatable checks for assembly contexts and resources retained after unloading.

Acceptance: Repeated unload cycles release their resources within a defined bound.

## New valuable feature

### FEAT-01: Expose plugin activation previews

Status: TODO.

Return a diagnostic report for a candidate domain without publishing it.

Acceptance: The report includes health and resource requirements while active traffic remains unchanged.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
