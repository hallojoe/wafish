# WafIsh.Plugin.Compression: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Expand encoding negotiation tests

Status: TODO.

Generate quality-value combinations, wildcards, and invalid Accept-Encoding values.

Acceptance: The selected encoding follows the supported negotiation rules.

### IMP-02: Measure streaming allocation costs

Status: TODO.

Compare large JSON and excluded binary responses under concurrent clients.

Acceptance: Compression retains bounded buffering and prompt first-byte delivery.

## New valuable feature

### FEAT-01: Expose compression savings metrics

Status: TODO.

Report input bytes, output bytes, and chosen encoding through bounded metric dimensions.

Acceptance: Operators can identify routes where compression increases cost without useful byte savings.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
