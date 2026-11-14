# Wafish.AppHost

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

Source project: [Wafish.AppHost.csproj](Wafish.AppHost.csproj).
Direct project references: [SampleGateway](../samples/SampleGateway/README.md), [SampleLocalService](../samples/SampleLocalService/README.md).

Start from the repository root:

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

See [topology options](../docs/aspire.md) and [SonarQube analysis](../docs/sonarqube.md).
The default topology needs no container runtime. Redis and SonarQube require one.

The resource table links to the gateway console at `/console` and backend details at `/api/local-service/instance`.
The gRPC endpoint appears only in resource details. It requires an HTTP/2 client and cannot open in a browser.
Service root paths have no page and return HTTP 404.
See [browser-link verification](../tests/Wafish.Regression/evidence/aspire-dashboard-links/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Isolated persistent state

`TopologyId` selects separate database directories, generated credentials, and optional container volumes.
The default identifier is `default`. Use `UseLegacyTopologyState=true` to reopen previous state.
The compatibility option requires the default identifier and never deletes existing volumes.
Run `python scripts/check-topology.py` for controlled restart verification.
SonarQube readiness accepts only an exact `UP` status. Invalid, unavailable, and timed-out responses remain unhealthy.
