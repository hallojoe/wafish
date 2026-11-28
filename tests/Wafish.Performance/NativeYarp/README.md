# NativeYarp

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](../../../PLAN.md) · [Project index](../../../docs/projects.md)

## Intro

This executable provides the bare YARP baseline for gateway performance comparisons.

```mermaid
flowchart LR
    P0["Performance runner"]
    P1["Bare YARP on port 5301"]
    P2["Backend on port 5300"]
    P3["Measured response"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

The runner sends the same payload through bare YARP and Wafish, then compares throughput and latency.

## Real-world value

A minimal baseline helps separate proxy overhead from additional gateway behavior.

## What it does

- Listen on loopback port 5301.
- Forward the performance route to the shared backend on port 5300.
- Expose a health endpoint for runner startup checks.

## What it does not do

- Load Wafish plugins, storage, or administration services.
- Act as evidence that unrelated production topologies have the same performance.

## Start here

Source project: [NativeYarp.csproj](NativeYarp.csproj).
This project has no direct solution project references. Package and framework references remain in its project file.

Use the [runner](../Runner/README.md) to coordinate this host with the shared backend.

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](../../../PLAN.md).

## Benchmark isolation and metadata

Listener and upstream origins must use loopback and match the selected transport.
The default remains exact HTTP/1.1 over cleartext.
See [matched transports](../Shared/README.md#matched-transports-r43) for HTTP/2, TLS, and certificate setup.
The runner validates workload status and payload before measurement.
Preflight has a five-second deadline and a four-MiB response limit.

`/benchmark/manifest` exports normalized configuration and a SHA-256 configuration hash.
Reports retain these manifests beside each measured configuration hash.
Secrets in named credential fields are redacted before hashing.

Set `--listen-url` and `--backend-url` to select isolated addresses.

## Multiple destinations

Use `--backend-urls` and `--load-balancing-policy` for bounded destination lists and explicit balancing.
The singular `--backend-url` remains supported.
See the [runner guide](../Runner/README.md#multiple-destinations-r48) for supported policies and matching transport requirements.
The effective cluster manifest includes all destinations and the selected policy.
