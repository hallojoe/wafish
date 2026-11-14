# Wafish.Plugin.Metrics

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

Source project: [Wafish.Plugin.Metrics.csproj](Wafish.Plugin.Metrics.csproj).
Direct project references: [Wafish.Plugin.Abstractions](../Wafish.Plugin.Abstractions/README.md), [Wafish](../Wafish/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Measurement semantics

Metrics count pipeline attempts that reach the metrics middleware. Each retry can produce another measurement.
Earlier cache hits and policy rejections do not reach this middleware.
A downstream circuit rejection can produce a measurement without an upstream request.
Each successful, throwing, or cancelled attempt records duration and one request count.
`Errors` counts HTTP 5xx results and exceptions. `Exceptions` identifies the exception subset.
`Cancellations` counts cancelled attempts separately from plugin errors.
Native gateway statistics use measured statuses 500 for exceptions and 499 for cancellation.
Native statistics include 499 in their error total. These measured statuses do not modify HTTP responses.
Byte totals use declared content lengths, not measured wire traffic.
Both plugins share one middleware installation. Cluster metrics also work without traffic metrics.

[Verification for review items 8–10](../../tests/Wafish.Regression/evidence/review8910/README.md).

## Review items 14–16

Metric snapshots retire identities when the active configuration version changes.
Late completions cannot recreate removed identities. Continuously active identities retain counters.
Reintroduced identities start at zero after retirement is observed. Process restart also resets counters.
Each route, cluster, and destination scope admits at most 10,000 series.
Existing series continue updating at capacity; new series are ignored until slots become available.
Reads and updates share a lock, preserving coherent snapshots during concurrent activity.

Verification: [review evidence](../../tests/Wafish.Regression/evidence/review141516/README.md).

## Concurrent snapshot checks

`--protocol-properties` runs eight writers against concurrent route, cluster, and destination snapshot reads.
Each seeded run records 2,048 outcomes and checks request, byte, duration, error, exception, and cancellation totals.
Explicit barriers expose partial snapshots without using delays to select interleavings.
Retired identities reject late writes. Reintroduced identities start with new counters.
