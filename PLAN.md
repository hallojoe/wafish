# Wafish remaining work

Updated: 2026-11-26. Baseline: `5d91599`.

This is the active backlog for this checkout. Unmerged branches do not establish completion.
R36–R55 are optional proposals. R56 and IDEA-01 remain deferred by user decision.
See [historical audits and completed work](ARCHIVE.md#archive-plan-md).

Complete each task only after implementation and acceptance checks pass.
Record compatibility impact and verification. Retain task IDs when priorities change.
Publication requires a separate release decision and exact artifact verification.

<a id="r56"></a>

## R56: Reconcile the release version and package publication

Status: DEFERRED (partly implemented). Reconcile source, release tag, and package feed before publication.

Verify feeds, versions, source identity, exact package consumers, and supported-platform checks.
Align metadata and release assets without replacing published versions or rewriting release history.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r56).

<a id="r45"></a>

## R45: Enforce CI performance budgets

Status: TODO. Acceptance: The runner reports regressions separately from request failures and measurement noise.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r45).

<a id="r38"></a>

## R38: Combine burst and sustained quotas

Status: TODO. Acceptance: Exceeding either allowance rejects the request.
Rejected requests do not partially consume another allowance.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r38).

<a id="r36"></a>

## R36: Share local quotas across named route groups

Status: TODO. Acceptance: Grouped routes share permits while unrelated routes remain isolated.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r36).

<a id="r40"></a>

## R40: Measure compression savings

Status: TODO. Acceptance: Operators can identify routes where compression increases cost without useful byte savings.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r40).

<a id="r41"></a>

## R41: Configure allowed compression algorithms

Status: PARTIAL. Acceptance: Disabled algorithms are never selected.
Quality values, identity fallback, and Vary handling remain correct.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r41).

<a id="r50"></a>

## R50: Demonstrate blue-green deployment

Status: TODO. Acceptance: The walkthrough proves cutover and rollback without losing the original route identity.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r50).

<a id="r48"></a>

## R48: Benchmark multiple destinations

Status: TODO. Acceptance: Reports identify destination count and distribution.
The baseline keeps its independence from Wafish packages.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r48).

<a id="r46"></a>

## R46: Add WebAuthn authentication

Status: TODO. Acceptance: Enrollment, challenge expiry, revocation, and recovery have integration tests.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r46).

<a id="r37"></a>

## R37: Partition quotas by trusted tenant

Status: TODO. Acceptance: Missing or untrusted claims follow a documented policy and cannot create unlimited key cardinality.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r37).

<a id="r39"></a>

## R39: Assign weighted request costs

Status: TODO. Acceptance: An expensive route consumes its configured allowance.
Invalid costs fail validation, and rejection reports usable retry information.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r39).

<a id="r49"></a>

## R49: Add optional network fault injection

Status: TODO. Acceptance: A repeatable scenario demonstrates retries and circuit recovery without changing production defaults.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r49).

<a id="r51"></a>

## R51: Demonstrate service discovery

Status: TODO. Acceptance: The default topology remains unchanged.
The discovery profile demonstrates stable destination identity and visible refresh failures.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r51).

<a id="r55"></a>

## R55: Save operator views

Status: TODO. Acceptance: Views restore after sign-in without storing credentials or sharing private preferences between users.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r55).

<a id="r52"></a>

## R52: Demonstrate cancellable streaming

Status: TODO. Acceptance: The first chunk arrives before completion.
Disconnecting the client stops backend work.
The sample demonstrates cache, compression, and retry streaming behavior.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r52).

<a id="r53"></a>

## R53: Show redacted telemetry configuration

Status: TODO. Acceptance: The view reports configuration state without revealing credentials or payload data.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r53).

<a id="r54"></a>

## R54: Demonstrate idempotent orders

Status: TODO. Acceptance: Repeated keys do not create duplicate sample orders, and conflicting payloads are rejected.

[Scope and evidence](ARCHIVE.md#archive-plan-md--r54).

<a id="idea-01"></a>

## IDEA-01: Separate administration and gateway processes

Status: DEFERRED. Promote when independent deployment or multi-gateway management is required.

Define identity, authorization, configuration ownership, delivery, recovery, storage, and protocol compatibility first.

[Scope and evidence](ARCHIVE.md#archive-plan-md--idea-01).
