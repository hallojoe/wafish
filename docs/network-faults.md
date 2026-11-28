# Local network fault scenario

The optional Aspire `fault-backend` resource reuses the benchmark backend's bounded fault fixtures.
It requires no container runtime and binds direct loopback HTTP.
It does not intercept ordinary sample traffic or add gateway routes automatically.

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableNetworkFaults=true
aspire wait fault-backend --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire describe --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

Use the assigned endpoint from the **Fault fixture** dashboard link. Remove `/benchmark/manifest` to obtain its origin.
The manifest confirms `transportFaults: true`. Do not assume a fixed port.

## Repeatable verification

When this AppHost is stopped, run:

```bash
python3 scripts/check-network-faults.py
```

The script refuses an already running target. It creates an isolated topology ID, temporary database directory, and generated credential.
It starts resources through Aspire, waits for health, and configures four isolated gateway routes with real plugin bindings.
It sends credentials only to the local administration API.
It then verifies:

1. Two transient HTTP 503 failures recover through two retries.
2. Two failures open a circuit. A blocked request never reaches the backend.
3. One successful recovery probe closes the circuit and restores normal forwarding.
4. A response stall reaches its deadline, cancels backend work, and permits later healthy requests.
5. A truncated response interrupts transport without being reported as a complete successful response.
6. Scenario routes and clusters are removed. Restarting without the flag removes the optional resource.

The script stops only its owned AppHost and removes its generated credential and temporary data.
It does not remove persistent container volumes or change existing topology data.

## Manual faults and recovery

Set `FAULT_BACKEND` to the assigned origin:

```bash
export FAULT_BACKEND=http://localhost:ASSIGNED_PORT
curl --fail "$FAULT_BACKEND/benchmark/manifest"
curl --fail "$FAULT_BACKEND/api/perf/control/counters"
```

Replace `ASSIGNED_PORT` before running commands.

| Endpoint | Behavior | Bounds and recovery |
| --- | --- | --- |
| `/api/perf/delay/1000` | Wait before sending the response. | Delay is clamped to 0–30,000 ms. Cancellation stops the delay. |
| `/api/perf/fault/stall/30000` | Start a one-byte response, then wait. | 1–30,000 ms. Client cancellation stops work. |
| `/api/perf/fault/truncate/1024` | Flush bytes, then abort before the declared length. | 1–1,048,576 bytes. The next request remains independent. |
| `/api/perf/flaky/2` | Return 503 for the first two attempts, then return `OK`. | Counter is shared by the fixture. Reset it between scenarios. |
| `/api/perf/plain` | Return `OK`. | Use after faults to confirm normal responses. |

Reset only when the counters report `active: 0`:

```bash
curl --fail -X POST "$FAULT_BACKEND/api/perf/control/reset"
```

Reset returns HTTP 409 while requests remain active. It does not clear gateway circuit state.
Do not send other fixture requests during assertions that depend on exact attempt counts.
The fixture's counters are process-local and reset when the resource restarts.

## Gateway bindings

Use separate exact-match routes and separate clusters for each scenario.
Point each cluster at the assigned fault backend origin, then set the route's `PathSet` transform.
The verification script provides executable examples of these API mutations.

| Route | PathSet | Binding |
| --- | --- | --- |
| `/fault-demo/retry` | `/api/perf/flaky/2` | Route `request-retry`: two retries, zero backoff and jitter, two-second attempt deadline, five-second total deadline. |
| `/fault-demo/circuit` | `/api/perf/flaky/2` | Cluster `circuit-breaker`: minimum throughput two, failure ratio one, two-second recovery timeout, one recovery probe. |
| `/fault-demo/stall` | `/api/perf/fault/stall/30000` | Route `request-retry`: one-second attempt and total deadlines. |
| `/fault-demo/truncate` | `/api/perf/fault/truncate/1024` | No retry binding. |

Keep control endpoints off proxy routes. They are intended for direct loopback access.
The fixture has no authentication and is not a production service.

Retries are appropriate before response transmission. They cannot reconstruct a partially delivered body.
A stall can produce HTTP 504 before the gateway commits headers, or an interrupted response after commitment.
A disconnect can produce HTTP 502 or an interrupted response, depending on when forwarding observes it.
Check both status and complete body delivery. A received HTTP 200 header does not prove a complete response.

For manual cleanup, remove only your scenario routes, clusters, and bindings.
Stop the AppHost normally, then restart without `EnableNetworkFaults` to remove the optional resource.
The default topology remains one gateway and two registered sample backends.

Source: [AppHost](../Wafish.AppHost/AppHost.cs), [fault backend](../tests/Wafish.Performance/Backend/Program.cs), and [verification script](../scripts/check-network-faults.py).
