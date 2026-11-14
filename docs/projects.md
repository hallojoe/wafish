# Solution project guide

Every project in [WafIsh.slnx](../WafIsh.slnx) has three documents:

- **README.md:** purpose, example, value, responsibilities, limits, and a Mermaid diagram.
- **AGENTS.md:** source map, implementation boundaries, and verification commands for coding agents.
- **PLAN.md:** unimplemented improvements and a proposed feature with acceptance checks.

AGENTS.md contains repository instructions. These projects do not need a separate reusable SKILL.md workflow.
Project plans are proposals. They do not replace the [root execution history](../PLAN.md) or [deferred ideas](../IDEAS.md).
Project READMEs describe the source projects. Current NuGet packaging still includes the root README.

## Main relationships

```mermaid
flowchart TD
    Host["SampleGateway"] --> UI["ReactUi"]
    UI --> Admin["Administration"]
    Admin --> Runtime["Runtime"]
    Runtime --> Core["WafIsh core"]
    Runtime --> Plugins["Policy plugins"]
    Plugins --> Core
    Host --> Sqlite["SQLite provider"]
    Sqlite --> Storage["Storage contracts"]
    Core --> Storage
    Backend["SampleLocalService"] --> Client["Client and gRPC contracts"]
    Client -. "registration" .-> Core
    AppHost["Aspire AppHost"] -. "starts" .-> Host
    AppHost -. "starts two instances" .-> Backend
```

Arrows show main dependencies or labeled runtime relationships, not every project reference.

## Projects

| Project | Purpose | Documentation |
| --- | --- | --- |
| `SampleGateway` | This executable combines the gateway runtime, SQLite storage, administration APIs, and the React workspace. | [README](../samples/SampleGateway/README.md) · [AGENTS](../samples/SampleGateway/AGENTS.md) · [PLAN](../samples/SampleGateway/PLAN.md) |
| `SampleLocalService` | This executable is a small backend that registers itself with a gateway. | [README](../samples/SampleLocalService/README.md) · [AGENTS](../samples/SampleLocalService/AGENTS.md) · [PLAN](../samples/SampleLocalService/PLAN.md) |
| `SampleServiceDefaults` | This library adds shared telemetry, service discovery, and health defaults to the sample hosts. | [README](../samples/SampleServiceDefaults/README.md) · [AGENTS](../samples/SampleServiceDefaults/AGENTS.md) · [PLAN](../samples/SampleServiceDefaults/PLAN.md) |
| `WafIsh.Administration` | This project provides administration APIs, authentication, realtime updates, and optional AI and MCP services. | [README](../src/WafIsh.Administration/README.md) · [AGENTS](../src/WafIsh.Administration/AGENTS.md) · [PLAN](../src/WafIsh.Administration/PLAN.md) |
| `WafIsh.Client` | This client registers an ASP.NET Core service with a gateway through HTTP or gRPC. | [README](../src/WafIsh.Client/README.md) · [AGENTS](../src/WafIsh.Client/AGENTS.md) · [PLAN](../src/WafIsh.Client/PLAN.md) |
| `WafIsh.Grpc` | This project defines the protobuf registration protocol and generates its .NET client and server types. | [README](../src/WafIsh.Grpc/README.md) · [AGENTS](../src/WafIsh.Grpc/AGENTS.md) · [PLAN](../src/WafIsh.Grpc/PLAN.md) |
| `WafIsh.Plugin.Abstractions` | This project defines the contracts shared by plugin implementations and the runtime host. | [README](../src/WafIsh.Plugin.Abstractions/README.md) · [AGENTS](../src/WafIsh.Plugin.Abstractions/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.Abstractions/PLAN.md) |
| `WafIsh.Plugin.Cache` | This plugin adds a bounded, in-memory HTTP response cache to selected routes. Its binding ID is `response-cache`. | [README](../src/WafIsh.Plugin.Cache/README.md) · [AGENTS](../src/WafIsh.Plugin.Cache/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.Cache/PLAN.md) |
| `WafIsh.Plugin.CircuitBreaker` | This plugin tracks destination failures and opens circuits for selected clusters. Its binding ID is `circuit-breaker`. | [README](../src/WafIsh.Plugin.CircuitBreaker/README.md) · [AGENTS](../src/WafIsh.Plugin.CircuitBreaker/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.CircuitBreaker/PLAN.md) |
| `WafIsh.Plugin.Compression` | This plugin compresses eligible route responses with Gzip or Brotli. Its binding ID is `compression`. | [README](../src/WafIsh.Plugin.Compression/README.md) · [AGENTS](../src/WafIsh.Plugin.Compression/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.Compression/PLAN.md) |
| `WafIsh.Plugin.Metrics` | This project supplies the `traffic-metrics` and `cluster-metrics` plugins with shared request measurements. | [README](../src/WafIsh.Plugin.Metrics/README.md) · [AGENTS](../src/WafIsh.Plugin.Metrics/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.Metrics/PLAN.md) |
| `WafIsh.Plugin.ProxyLog` | This plugin captures proxy metadata and optional payloads. Its binding ID is `proxy-log`. | [README](../src/WafIsh.Plugin.ProxyLog/README.md) · [AGENTS](../src/WafIsh.Plugin.ProxyLog/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.ProxyLog/PLAN.md) |
| `WafIsh.Plugin.RateLimit.Redis` | This plugin uses Redis Lua operations for shared route limits. Its binding ID is `rate-limit-redis`. | [README](../src/WafIsh.Plugin.RateLimit.Redis/README.md) · [AGENTS](../src/WafIsh.Plugin.RateLimit.Redis/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.RateLimit.Redis/PLAN.md) |
| `WafIsh.Plugin.RateLimit` | This plugin limits route traffic inside one gateway process. Its binding ID is `rate-limit`. | [README](../src/WafIsh.Plugin.RateLimit/README.md) · [AGENTS](../src/WafIsh.Plugin.RateLimit/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.RateLimit/PLAN.md) |
| `WafIsh.Plugin.Retry` | This plugin retries eligible upstream failures according to route policy. Its binding ID is `request-retry`. | [README](../src/WafIsh.Plugin.Retry/README.md) · [AGENTS](../src/WafIsh.Plugin.Retry/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.Retry/PLAN.md) |
| `WafIsh.Plugin.ServiceDiscovery` | This plugin refreshes cluster destinations from supported discovery sources. Its binding ID is `service-discovery`. | [README](../src/WafIsh.Plugin.ServiceDiscovery/README.md) · [AGENTS](../src/WafIsh.Plugin.ServiceDiscovery/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.ServiceDiscovery/PLAN.md) |
| `WafIsh.Plugin.Waf` | This plugin inspects route requests before forwarding. Its binding ID is `waf`. | [README](../src/WafIsh.Plugin.Waf/README.md) · [AGENTS](../src/WafIsh.Plugin.Waf/AGENTS.md) · [PLAN](../src/WafIsh.Plugin.Waf/PLAN.md) |
| `WafIsh.ReactUi` | This project embeds the React administration workspace in an ASP.NET Core package. | [README](../src/WafIsh.ReactUi/README.md) · [AGENTS](../src/WafIsh.ReactUi/AGENTS.md) · [PLAN](../src/WafIsh.ReactUi/PLAN.md) |
| `WafIsh.Runtime` | This project assembles gateway services, plugin lifecycles, and the request pipeline without a browser interface. | [README](../src/WafIsh.Runtime/README.md) · [AGENTS](../src/WafIsh.Runtime/AGENTS.md) · [PLAN](../src/WafIsh.Runtime/PLAN.md) |
| `WafIsh.Storage.Abstractions` | This project defines storage interfaces and data records used by gateway and administration services. | [README](../src/WafIsh.Storage.Abstractions/README.md) · [AGENTS](../src/WafIsh.Storage.Abstractions/AGENTS.md) · [PLAN](../src/WafIsh.Storage.Abstractions/PLAN.md) |
| `WafIsh.Storage.Sqlite` | This project implements the storage contracts with SQLite and optional SQLCipher-compatible encryption through SQLite3MC. | [README](../src/WafIsh.Storage.Sqlite/README.md) · [AGENTS](../src/WafIsh.Storage.Sqlite/AGENTS.md) · [PLAN](../src/WafIsh.Storage.Sqlite/PLAN.md) |
| `WafIsh` | The gateway core connects YARP routing, configuration changes, registration, and control-plane access rules. | [README](../src/WafIsh/README.md) · [AGENTS](../src/WafIsh/AGENTS.md) · [PLAN](../src/WafIsh/PLAN.md) |
| `WafIsh.Administration.Regression` | This executable verifies administration APIs against isolated running hosts and real storage. | [README](../tests/WafIsh.Administration.Regression/README.md) · [AGENTS](../tests/WafIsh.Administration.Regression/AGENTS.md) · [PLAN](../tests/WafIsh.Administration.Regression/PLAN.md) |
| `WafIsh.ReactUi.Host` | This test-only ASP.NET Core host supplies real administration APIs for browser verification. | [README](../tests/WafIsh.ReactUi.Host/README.md) · [AGENTS](../tests/WafIsh.ReactUi.Host/AGENTS.md) · [PLAN](../tests/WafIsh.ReactUi.Host/PLAN.md) |
| `WafIsh.Regression` | This executable runs the main gateway regression checks. It uses a custom runner rather than a unit-test framework runner. | [README](../tests/WafIsh.Regression/README.md) · [AGENTS](../tests/WafIsh.Regression/AGENTS.md) · [PLAN](../tests/WafIsh.Regression/PLAN.md) |
| `Backend` | This executable is the controlled upstream service used by the performance harness. | [README](../tests/WafIsh.Performance/Backend/README.md) · [AGENTS](../tests/WafIsh.Performance/Backend/AGENTS.md) · [PLAN](../tests/WafIsh.Performance/Backend/PLAN.md) |
| `EnhancedYarp` | This executable hosts WafIsh variants for performance and resilience scenarios. | [README](../tests/WafIsh.Performance/EnhancedYarp/README.md) · [AGENTS](../tests/WafIsh.Performance/EnhancedYarp/AGENTS.md) · [PLAN](../tests/WafIsh.Performance/EnhancedYarp/PLAN.md) |
| `NativeYarp` | This executable provides the bare YARP baseline for gateway performance comparisons. | [README](../tests/WafIsh.Performance/NativeYarp/README.md) · [AGENTS](../tests/WafIsh.Performance/NativeYarp/AGENTS.md) · [PLAN](../tests/WafIsh.Performance/NativeYarp/PLAN.md) |
| `Runner` | This executable drives repeatable load scenarios and writes performance comparison reports. | [README](../tests/WafIsh.Performance/Runner/README.md) · [AGENTS](../tests/WafIsh.Performance/Runner/AGENTS.md) · [PLAN](../tests/WafIsh.Performance/Runner/PLAN.md) |
| `WafIsh.AppHost` | This Aspire AppHost runs the local sample topology and optional development infrastructure. | [README](../WafIsh.AppHost/README.md) · [AGENTS](../WafIsh.AppHost/AGENTS.md) · [PLAN](../WafIsh.AppHost/PLAN.md) |
