# WafIsh.Plugin.Cache: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Expand cache-header property tests

Status: TODO.

Generate combinations of freshness, Vary, Age, and request cache directives.

Acceptance: Generated cases preserve isolation and freshness requirements.

### IMP-02: Measure concurrent capture pressure

Status: TODO.

Benchmark simultaneous misses and eviction while clients read cached entries.

Acceptance: The configured budget bounds retained memory under concurrent load.

## New valuable feature

### FEAT-01: Add route-scoped cache tags and purge

Status: TODO.

Allow authenticated administration to invalidate tagged public responses.

Acceptance: Purging one route or tag leaves unrelated entries available.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
