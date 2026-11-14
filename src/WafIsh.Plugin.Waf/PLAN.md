# WafIsh.Plugin.Waf: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add fuzz tests for decoding and normalization

Status: TODO.

Generate encoded paths, Unicode bodies, malformed escapes, and split reads.

Acceptance: Checks terminate within bounds and preserve the documented rejection behavior.

### IMP-02: Measure false positives with a benign corpus

Status: TODO.

Maintain representative valid API payloads alongside attack fixtures.

Acceptance: Rule changes report both missed attack fixtures and rejected benign fixtures.

## New valuable feature

### FEAT-01: Add an explicit detection-only mode

Status: TODO.

Allow selected rules to report matches without blocking during policy evaluation.

Acceptance: Blocking remains the default, and reports clearly identify non-blocking decisions.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
