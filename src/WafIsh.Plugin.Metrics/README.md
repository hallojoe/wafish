# WafIsh.Plugin.Metrics

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This project supplies the `traffic-metrics` and `cluster-metrics` plugins with shared request measurements.

```mermaid
flowchart LR
    P0["Proxy request"]
    P1["Metrics middleware"]
    P2["In-process snapshots"]
    P3["Administration views"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

An operator compares error rates and latency for an orders route and its destination cluster after a release.

## Real-world value

Request measurements help identify which route or backend needs investigation.

## What it does

- Collect request, error, latency, and supported byte measurements.
- Apply route and cluster metric bindings.
- Expose in-process snapshots consumed by administration services.

## What it does not do

- Provide a durable time-series database.
- Replace distributed tracing or automatically provision a metrics backend.

## Start here

Source project: [WafIsh.Plugin.Metrics.csproj](WafIsh.Plugin.Metrics.csproj).
Direct project references: [WafIsh.Plugin.Abstractions](../WafIsh.Plugin.Abstractions/README.md), [WafIsh](../WafIsh/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).
