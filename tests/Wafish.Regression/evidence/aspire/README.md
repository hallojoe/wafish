# Aspire verification

Tasks: ASP-01 through ASP-08. Owner: Codex. Date: 2026-11-14.
Tested source: working tree based on commit `6ba871a`; changes remain uncommitted.

The AppHost runs a gateway and two backend processes with dynamic HTTP and gRPC endpoints.
SQLite files remain under the ignored AppHost `.data` directory.
A generated user-secret parameter supplies shared Basic authentication credentials.

Verified checks:

- Basic startup: gateway and both registered backend instances became healthy.
- Load balancing: requests reached both instance identities through one shared cluster.
- Backend stop and restart: routing used the remaining backend, then restored both without duplicate destinations.
- Gateway restart: persisted configuration and backend registrations recovered.
- Telemetry: one request produced correlated gateway and backend spans, including the selected destination identifier.
- Missing administration credentials returned HTTP 401.
- Optional Redis and a second gateway ran through rootless Podman.
- A shared three-permit policy returned 200, 200, 200, then 429 across both gateways.
- During Redis outage, FailClosed returned 503; explicit FailOpen returned 200.
- Redis recovery restored shared counters and rate-limit enforcement.
- External runner mode completed without terminating any managed services.
- Existing managed runner scenarios completed. These are smoke checks, not performance comparisons.
- Normal AppHost shutdown succeeded and retained persistent files and the Redis volume.
- React login and realtime updates passed with the injected Basic credentials.
- Development mode loads Razor fallback assets. Its JSON worker validates without missing Monaco resources.
- Two-factor state uses a separate persistent path for each gateway.

Commands:

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire wait gateway --timeout 60 --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire wait backend-one --timeout 60 --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire wait backend-two --timeout 60 --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire describe --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire stop --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
ASPIRE_CONTAINER_RUNTIME=podman aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableRedis=true --EnableSecondGateway=true
dotnet run --project tests/Wafish.Performance/Runner -c Release -- --external-url <resolved-gateway-url> --duration 1 --warmup 0 --concurrency 2 --repetitions 1
dotnet run --project tests/Wafish.Performance/Runner -c Release -- --duration 1 --warmup 0 --concurrency 2 --repetitions 1
```

Artifacts: [readiness](basic-readiness.log), [resources](resources.json), [backend recovery](backend-restart.log),
[gateway recovery](gateway-restart.log), [correlated trace](trace.json), [Redis recovery](redis-recovery.log),
[external runner](external-runner.log), [managed runner](managed-runner.log), and [shutdown](shutdown.log).
Sensitive dashboard URLs, environment secrets, and telemetry keys are excluded from retained evidence.

Limitations: production deployment is outside this task. The native database blocker was subsequently resolved under [SEC-14](../sec14/README.md).
Process CPU and memory are unavailable in external runner mode; its report identifies those fields as unavailable.
