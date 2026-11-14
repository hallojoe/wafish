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

The default listener is `http://127.0.0.1:5300`; override it with `--listen-url`.
Run scenarios through the [performance runner](../Runner/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Benchmark isolation and metadata

Listener and upstream options accept loopback HTTP origins only.
Native and Wafish upstream connections use HTTP/1.1 with an exact version policy.
The runner validates workload status and payload before measurement.
Preflight has a five-second deadline and a four-MiB response limit.

`/benchmark/manifest` exports normalized configuration and a SHA-256 configuration hash.
Reports retain these manifests beside each measured configuration hash.
Secrets in named credential fields are redacted before hashing.

`--payload-mode=precomputed` prepares common payload sizes before requests.
`--payload-mode=generated` includes payload allocation in each request.
`X-Benchmark-PayloadMode` identifies the actual strategy for each payload response.
Echo accepts at most four MiB. Delay accepts at most 30 seconds.
Both operations stop when clients disconnect.
Counters distinguish active, completed, cancelled, and aborted requests.
Reset returns HTTP 409 while work remains active.

## Transport fault fixtures

Pass `--transport-faults=true` to enable fault endpoints. They remain unavailable by default.
`/api/perf/fault/truncate/{bytes}` writes 1–1,048,576 bytes, advertises one additional byte, then aborts the connection.
`/api/perf/fault/stall/{milliseconds}` sends headers, waits 1–30,000 milliseconds, then sends one byte.
A cancelled body read stops the stalled request. Invalid bounds return 400.
The backend binds only to an explicitly validated loopback address.
Counters classify injected disconnects as aborted and caller-cancelled stalls as cancelled.
Reset through `/api/perf/control/reset` after active requests reach zero. Reset during active work returns 409.
The benchmark manifest includes the fault-mode setting. These endpoints require explicit fault workloads, not normal success preflight.
