# Backend

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../../docs/projects.md)

## Intro

This executable is the controlled upstream service used by the performance harness.

```mermaid
flowchart LR
    P0["Benchmark request"]
    P1["Backend on loopback port 5300"]
    P2["Selected payload or failure behavior"]
    P3["Request counters"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

The runner requests a small JSON response or a deliberately failing endpoint through each gateway under comparison.

## Real-world value

A common upstream makes gateway overhead and retry effects easier to compare.

## What it does

- Serve plain text, JSON, bounded payloads, echo, delay, status, and flaky-response endpoints.
- Record request and status counters.
- Expose reset and counter endpoints for functional benchmark assertions.

## What it does not do

- Represent a complete production workload.
- Provide authentication or a public administration interface for benchmark controls.

## Start here

Source project: [Backend.csproj](Backend.csproj).
This project has no direct solution project references. Package and framework references remain in its project file.

The managed runner expects this backend at `http://127.0.0.1:5300`.
Run scenarios through the [performance runner](../Runner/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).
