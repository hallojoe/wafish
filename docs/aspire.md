# Local Aspire topology

Requires .NET 10, Node.js for the React UI build, and the Aspire CLI.
The basic topology requires no container runtime.
The optional Redis topology requires Docker or Podman.
The optional [SonarQube setup](sonarqube.md) also requires Docker or Podman.

Start the sample:

```bash
aspire start --apphost WafIsh.AppHost/WafIsh.AppHost.csproj --non-interactive
aspire wait gateway --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
aspire wait backend-one --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
aspire wait backend-two --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
aspire describe --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
```

Use the reported gateway HTTP URL. Do not assume fixed service ports.
`/api/local-service/instance` identifies the selected backend and trace.
The two instances share one service cluster and the same route definitions.
HTTP and gRPC use separate dynamically assigned loopback endpoints.
The gRPC endpoint uses HTTP/2 cleartext. Production remote administration requires HTTPS.
The Aspire dashboard itself uses the generated HTTPS launch profile.
Trust the development certificate through your platform's normal certificate setup.

## Credentials

The AppHost generates `control-plane-password` and persists it in its user-secrets store.
The gateway and registration clients receive it as a secret environment value.
The username is `admin`. The local topology uses shared Basic authentication.
Reveal the secret parameter in the Aspire dashboard when logging in.
The AppHost never embeds the credential in process arguments or committed settings.

Override the generated parameter through `Parameters:control-plane-password` in the AppHost's secret provider.
For automation, inject `Parameters__control-plane-password` through the process environment.
Do not commit it or paste it into command arguments.

## Data and lifetime

SQLite stays local. The primary database is `WafIsh.AppHost/.data/gateway.db`.
`GatewayDataDirectory` can specify another absolute directory.
The directory is ignored by Git and survives normal AppHost shutdown.
An optional second gateway uses `gateway-secondary.db` in the same directory.
Never run two AppHosts against the same gateway database simultaneously.
Use distinct `GatewayDataDirectory` values for isolated runs.

Stable `Gateway:Registration:InstanceId` values own each instance's destinations.
Restart replaces that instance's addresses. Shutdown removes only its destinations.
The final instance removes shared routes and the orphaned cluster.
Instance registration requires gRPC. Legacy registration remains available without InstanceId.
All instances must agree on service ID, cluster, paths, and balancing policy.
The shared registration mode enables active `/health` probes. Backends must expose that endpoint.

`Gateway:Registration:GrpcUrl` overrides the old port-plus-one convention.
`AutoResolveIp=false` preserves the injected loopback address.
Untrusted TLS certificates are rejected if HTTPS registration is selected.

## Optional Redis and multiple gateways

```bash
aspire start --apphost WafIsh.AppHost/WafIsh.AppHost.csproj --non-interactive -- --EnableRedis=true --EnableSecondGateway=true
```

For Podman, prefix the start command with `ASPIRE_CONTAINER_RUNTIME=podman`.
The typed Redis resource manages its ports, credentials, TLS, and data volume.
Its connection string is injected into `Gateway:RateLimit:Redis:ConnectionString`.
SQLite remains the gateway configuration store.

The second gateway is optional and uses isolated configuration storage.
It routes to the same backends through endpoint references.
Bind `rate-limit-redis` to `sample-service-path0` on both gateways to test shared enforcement.
Use identical limits, algorithms, windows, and key prefixes.
The default failure policy returns 503 during Redis outages. Explicit `FailOpen` permits requests.
Exhausted limits return 429. See [security configuration](security.md).

## Health, logs, and traces

The gateway becomes ready before backends register. This avoids a startup dependency cycle.
Backends wait for gateway readiness. The second gateway waits for the backends.
Readiness does not mean every backend is healthy.
Active destination probes remove failed instances from eligible routing.

SampleServiceDefaults adds tracing, metrics, logs, and service discovery.
It does not add HTTP resilience handlers or change retry policies.
YARP spans include destination IDs. Backend spans include `gateway.destination.instance`.
The same trace connects the gateway and destination.
Query redaction remains enabled. AI message capture is disabled.

```bash
aspire otel traces gateway --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
aspire otel logs gateway --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
aspire resource backend-one restart --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
aspire wait backend-one --apphost WafIsh.AppHost/WafIsh.AppHost.csproj
```

The Aspire dashboard controls processes and shows telemetry.
The gateway administration UI edits routes, clusters, plugins, and gateway settings.
Neither dashboard replaces the other.

## Performance runner

Use the reported gateway URL in external mode:

```bash
dotnet run --project tests/WafIsh.Performance/Runner -c Release -- \
  --external-url http://localhost:PORT --path /api/local-service/instance \
  --duration 10 --warmup 3 --concurrency 16 --repetitions 3
```

External mode never starts, stops, or cleans service processes or databases.
CPU and process memory are unavailable in this mode. Reported zero values mean unavailable.
Use identical telemetry settings for comparisons. A short smoke run is not a performance conclusion.
Without `--external-url`, the existing process-managed benchmark modes remain available.

## Shutdown and troubleshooting

```bash
aspire stop --apphost WafIsh.AppHost/WafIsh.AppHost.csproj --non-interactive
```

Normal shutdown preserves local SQLite data and the optional Redis volume.
Do not use `--force` for normal shutdown.

- Registration failures: inspect gateway authentication settings and backend gRPC URLs.
- Missing routes: inspect backend logs and verify InstanceId and shared paths.
- Port conflicts: use assigned endpoint references; do not enable automatic Kestrel port injection under Aspire.
- Redis unavailable: inspect its health and TLS trust before changing the connection policy.
- Docker permission errors: use an available configured Podman runtime or correct Docker permissions outside the app.
- Certificate failures: fix platform trust; do not disable validation.

Verified with Aspire CLI 13.6.4 and AppHost/hosting packages 13.6.5.
See [verification evidence](../tests/WafIsh.Regression/evidence/aspire/README.md).
