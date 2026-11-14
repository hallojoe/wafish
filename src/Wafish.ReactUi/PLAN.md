# Wafish.ReactUi: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Expand page-level component coverage

Status: TODO.

Cover draft preservation, stale requests, and loading-to-error transitions for each workspace.

Acceptance: Each workspace has tests beyond the shared API client.

### IMP-02: Check accessibility across dynamic states

Status: TODO.

Test dialogs, validation errors, realtime disconnects, and mobile navigation.

Acceptance: Keyboard and automated accessibility checks pass for these states.

## New valuable feature

### FEAT-01: Add saved operator views

Status: TODO.

Persist named filters and column choices through an authenticated per-user API.

Acceptance: Views restore after sign-in without storing credentials or sharing private preferences between users.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include implementation and acceptance evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Include the hosting path base

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: hosting gap.

Verification: [review items 11–13 evidence](../../tests/Wafish.Regression/evidence/review111213/README.md).
Release build, 163 regression executions, and 20 browser checks pass.
Changed C# source coverage is 93.85%. Asset-validator coverage is 100%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [ReactUiExtensions.cs](ReactUiExtensions.cs), `MapWafishReactUi` and `Shell`.

Injected UI and API URLs start at the origin root.
They do not include `HttpRequest.PathBase`.
A host mounted under a prefix can request assets and APIs from incorrect locations.

Acceptance: Hosting under `/edge` produces working UI, asset, and API URLs.
Custom UI and administration prefixes still work together.

### IMP-04: Complete frontend build inputs

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: build reproducibility gap.

Verification: [review items 11–13 evidence](../../tests/Wafish.Regression/evidence/review111213/README.md).
Release build, 163 regression executions, and 20 browser checks pass.
Changed C# source coverage is 93.85%. Asset-validator coverage is 100%.
Base commit `bc1b617`; implementation remains uncommitted.
The original finding and acceptance criteria follow.

Evidence: [Wafish.ReactUi.csproj](Wafish.ReactUi.csproj), `FrontendSource` and `BuildFrontend`.

The incremental inputs omit `ClientApp/package.json`.
Changing a build script can leave previously generated assets embedded.
Include every build-driving input and verify asset completeness before packing.

Acceptance: Changing the build script triggers an asset rebuild.
Packing fails clearly when required generated assets are missing.

### FEAT-02: Expose embedded asset version metadata

Status: TODO. Priority: P3. Type: hosting feature.

Evidence: [ReactUiExtensions.cs](ReactUiExtensions.cs) injects host configuration into the HTML shell.

Include an asset build identifier and compatible administration contract version.
This lets clients detect stale assets after gateway updates.

Acceptance: Published assemblies expose reproducible identifiers.
Metadata contains no environment secrets or local filesystem paths.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
