# Runner

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../../docs/projects.md)

## Intro

This executable drives repeatable load scenarios and writes performance comparison reports.

```mermaid
flowchart LR
    P0["Scenario and load settings"]
    P1["Managed hosts or external endpoint"]
    P2["Repeated request measurements"]
    P3["CSV and Markdown reports"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

A developer compares bare YARP and selected WafIsh modes using repeated warmup and measurement phases.

## Real-world value

Reports connect latency, throughput, errors, and available process measurements to a defined scenario.

## What it does

- Run managed benchmark hosts or target an existing endpoint in external mode.
- Control warmup, duration, concurrency, repetition, and supported scenario suites.
- Write CSV and Markdown results under tests/WafIsh.Performance/results.

## What it does not do

- Treat external-mode zero CPU or memory values as measured zero usage.
- Produce statistically portable production capacity guarantees from one local run.

## Start here

Source project: [Runner.csproj](Runner.csproj).
This project has no direct solution project references. Package and framework references remain in its project file.

A short managed run, from the repository root:

```bash
dotnet build WafIsh.slnx -c Release
dotnet run --project tests/WafIsh.Performance/Runner -c Release --no-build -- --duration 2 --warmup 1 --concurrency 8 --repetitions 1
```

Managed mode uses loopback ports 5300–5302.
For an existing Aspire gateway, use `--external-url` with its reported URL and an appropriate `--path`.
External mode leaves the target services running.

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Review fixes 4–7

Managed and external failures return nonzero exit codes. Rate-limit scenarios accept only HTTP 429 as expected rejection.
Reports separate expected rejections from transport failures. Each request has a ten-second deadline covering headers and response reads.
