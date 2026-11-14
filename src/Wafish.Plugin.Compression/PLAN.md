# Wafish.Plugin.Compression: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Expand encoding negotiation tests

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [generated protocol evidence](../../tests/Wafish.Regression/evidence/protocol-properties/README.md).

Generate quality-value combinations, wildcards, and invalid Accept-Encoding values.

Acceptance: The selected encoding follows the supported negotiation rules.

### IMP-02: Measure streaming allocation costs

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [allocation pressure](../../tests/Wafish.Regression/evidence/allocation-pressure/README.md).

Compare large JSON and excluded binary responses under concurrent clients.

Acceptance: Compression retains bounded buffering and prompt first-byte delivery.

## New valuable feature

### FEAT-01: Expose compression savings metrics

Status: TODO.

Report input bytes, output bytes, and chosen encoding through bounded metric dimensions.

Acceptance: Operators can identify routes where compression increases cost without useful byte savings.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include implementation and acceptance evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Explain the unknown-length compression threshold

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: configuration mismatch.

Verification: [review items 11–13 evidence](../../tests/Wafish.Regression/evidence/review111213/README.md).
Release build, 163 regression executions, and 20 browser checks pass.
Changed C# source coverage is 93.85%. Asset-validator coverage is 100%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [CompressionPlugin.cs](CompressionPlugin.cs) and [CompressionMiddleware.cs](CompressionMiddleware.cs), `CompressionWriteStream.WriteAsync`.

The schema permits a ten-MiB minimum response size.
Unknown-length responses use at most 64 KiB of prefix storage.
Large thresholds can therefore bypass compression before the threshold is reached.
Define this fallback explicitly in schema descriptions and diagnostics.

Acceptance: Tests cover thresholds below, at, and above 64 KiB.
Identical payloads have documented behavior for known and unknown lengths.

### FEAT-02: Configure allowed compression algorithms

Status: TODO. Priority: P3. Type: feature.

Evidence: [CompressionMiddleware.cs](CompressionMiddleware.cs), `SelectEncoding`, always considers Brotli and Gzip.

Allow route policy to select supported algorithms and their compression levels.
This permits explicit CPU and bandwidth tradeoffs.

Acceptance: Disabled algorithms are never selected.
Quality values, identity fallback, and Vary handling remain correct.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
