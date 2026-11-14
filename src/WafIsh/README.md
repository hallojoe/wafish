# WafIsh

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../docs/projects.md)

## Intro

The gateway core connects YARP routing, configuration changes, registration, and control-plane access rules.

```mermaid
flowchart LR
    P0["Registration or configuration change"]
    P1["WafIsh validation and compilation"]
    P2["Storage contract and version check"]
    P3["Active YARP snapshot"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

An orders service gains a second instance. The gateway updates destinations while existing requests continue using their active configuration.

## Real-world value

Applications can change routing without replacing their gateway host or restarting every backend.

## What it does

- Compile route, cluster, and plugin configuration into versioned snapshots.
- Validate and persist configuration changes before activation.
- Expose REST and gRPC registration support with shared access rules.

## What it does not do

- Provide the React interface or concrete SQLite repositories.
- Replace runtime service registration from WafIsh.Runtime.

## Start here

Source project: [WafIsh.csproj](WafIsh.csproj).
Direct project references: [WafIsh.Client](../WafIsh.Client/README.md), [WafIsh.Grpc](../WafIsh.Grpc/README.md), [WafIsh.Plugin.Abstractions](../WafIsh.Plugin.Abstractions/README.md), [WafIsh.Storage.Abstractions](../WafIsh.Storage.Abstractions/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).
