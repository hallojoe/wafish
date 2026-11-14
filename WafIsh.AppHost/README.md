# WafIsh.AppHost

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../docs/projects.md)

## Intro

This Aspire AppHost runs the local sample topology and optional development infrastructure.

```mermaid
flowchart TD
    AppHost["Aspire AppHost"] --> Gateway["SampleGateway"]
    AppHost --> Backends["Two SampleLocalService instances"]
    Gateway -->|"proxy requests"| Backends
    Gateway --> Data["Persistent SQLite data"]
    Gateway -. "telemetry" .-> Dashboard["Aspire dashboard"]
    Backends -. "telemetry" .-> Dashboard
    AppHost -. "EnableSecondGateway" .-> Second["Second gateway"]
    AppHost -. "EnableRedis" .-> Redis["Redis"]
    AppHost -. "EnableSonarQube" .-> Sonar["SonarQube"]
    Sonar --> Postgres["PostgreSQL"]
```

## Real-world example

A developer starts one gateway and two backends, then uses the dashboard to trace a request across them.

## Real-world value

One topology supplies endpoint wiring, generated credentials, health dependencies, and local observability.

## What it does

- Start a gateway and two backend instances with assigned endpoints.
- Optionally add a second gateway, Redis, and SonarQube with PostgreSQL.
- Persist sample gateway data and generated secret parameters.

## What it does not do

- Run a SonarQube analysis merely by starting the server.
- Deploy a production environment or add Aspire dependencies to reusable libraries.

## Start here

Source project: [WafIsh.AppHost.csproj](WafIsh.AppHost.csproj).
Direct project references: [SampleGateway](../samples/SampleGateway/README.md), [SampleLocalService](../samples/SampleLocalService/README.md).

Start from the repository root:

```bash
aspire start --apphost WafIsh.AppHost/WafIsh.AppHost.csproj --non-interactive
```

See [topology options](../docs/aspire.md) and [SonarQube analysis](../docs/sonarqube.md).
The default topology needs no container runtime. Redis and SonarQube require one.

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).
