# WafIsh.Plugin.ServiceDiscovery

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This plugin refreshes cluster destinations from supported discovery sources. Its binding ID is `service-discovery`.

```mermaid
flowchart LR
    P0["Cluster binding"]
    P1["Discovery resource"]
    P2["Registry response and endpoint normalization"]
    P3["Updated gateway destinations"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

A gateway resolves available inventory instances from Consul and publishes updated destinations when instances change.

## Real-world value

Operators can use an existing service registry instead of maintaining every destination manually.

## What it does

- Resolve Static, HttpJson, Consul, Nacos, Eureka, and Kubernetes sources.
- Refresh enabled cluster bindings through a managed background resource.
- Normalize endpoints and publish changed destination sets.

## What it does not do

- Operate a registry server or deploy discovered services.
- Implement Kubernetes EndpointSlice watches in the current provider.

## Start here

Source project: [WafIsh.Plugin.ServiceDiscovery.csproj](WafIsh.Plugin.ServiceDiscovery.csproj).
Direct project references: [WafIsh.Plugin.Abstractions](../WafIsh.Plugin.Abstractions/README.md), [WafIsh](../WafIsh/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Review fixes 4–7

Destination IDs use SHA-256 over normalized absolute addresses. URI path case remains significant.
Publication checks the captured binding and shares the configuration mutation gate.
Unrelated configuration changes preserve the last discovered destinations. Removed or changed bindings reject stale refresh results.

## Review items 14–16

Injected `TimeProvider` controls refresh intervals and loop delays.
Refresh intervals use monotonic elapsed time; resource health timestamps use UTC.

Verification: [review evidence](../../tests/WafIsh.Regression/evidence/review141516/README.md).
