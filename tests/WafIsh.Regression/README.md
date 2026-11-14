# WafIsh.Regression

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This executable runs the main gateway regression checks. It uses a custom runner rather than a unit-test framework runner.

```mermaid
flowchart LR
    P0["Custom regression runner"]
    P1["Scenario fixtures"]
    P2["Gateway and storage packages"]
    P3["Pass output or failing exit code"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

A developer changes snapshot persistence and runs this project to check rollback, registration, storage, and request behavior.

## Real-world value

The suite catches behavior changes across package boundaries before a release.

## What it does

- Exercise configuration transactions, security boundaries, registration, plugins, storage, and streaming.
- Use real temporary SQLite databases and focused HTTP fixtures.
- Provide storage-only and live Redis modes, plus an administration route contract export.

## What it does not do

- Run through dotnet test as a conventional test-framework project.
- Replace browser workflows, administration integration checks, or performance measurements.

## Start here

Source project: [WafIsh.Regression.csproj](WafIsh.Regression.csproj).
Direct project references: [WafIsh](../../src/WafIsh/README.md), [WafIsh.Administration](../../src/WafIsh.Administration/README.md), [WafIsh.Storage.Sqlite](../../src/WafIsh.Storage.Sqlite/README.md).

Options: `--storage`, `--redis`, and `--dashboard-routes`.
Live Redis checks require `ANEIANG_REDIS_SERVER` to identify a Redis server executable.

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Review regression coverage

Live Kestrel checks cover retry, cache, compression, YARP replay, response-start boundaries, and disconnect cancellation.

Review items 4–7 add deterministic publication barriers, atomic binding batches, live circuit retries, and lifecycle checks.
Run `--review-lifecycle` for focused checks. [Evidence](evidence/review4567/README.md) includes managed benchmarks and live Redis verification.

Review items 8–10 add log shutdown accounting, notification classification, UTC queries, orphan cleanup, and metrics finalization.
Run `--review-accounting` for ten focused checks. [Evidence](evidence/review8910/README.md) records full regression and coverage results.

Review items 11–13 add prefixed shell checks, owned domain shutdown, cache-key compatibility, and compression threshold cases.
Run `--review-deployment` for six focused checks.
[Evidence](evidence/review111213/README.md) also records browser, asset validation, and package checks.

## Machine-readable results

Set `WAFISH_TEST_REPORT` to a JSON output path before running the executable.
Schema version 1 includes suite, case name, duration, status, and exception details.
Normal exits write reports, including focused modes and failed assertions.
Process termination cannot guarantee a report. CI also retains console logs.
Use `--review-release` for controlled-clock, retention, and UTC migration checks.
