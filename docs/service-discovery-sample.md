# HTTP JSON discovery sample

`EnableServiceDiscovery=true` adds a local catalog for the two sample backends.
The existing gRPC registration and ordinary sample route remain independent.
The optional catalog does not change production defaults or automatically create gateway bindings.

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableServiceDiscovery=true
aspire wait discovery-catalog --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire describe --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

Use **HTTP JSON catalog** to find `/catalog`. Remove that path to obtain the catalog origin.
All endpoints use assigned loopback HTTP ports. No container runtime is required.

## Repeatable verification

When the target AppHost is stopped:

```bash
python3 scripts/check-service-discovery.py
```

The script refuses an existing AppHost. It starts an isolated topology with temporary state and generated credentials.
It creates a separate `discovery-demo` cluster and route with a real `service-discovery` binding.
It verifies both backends, membership changes, stable destination IDs, stale handling, health diagnostics, and recovery.
A healthy empty response removes destinations without being classified as a registry failure.
It also checks that the ordinary gRPC route remains available.
Finally, it removes its configuration, stops its AppHost, removes its credential and temporary data, and verifies the default topology.

## Configure the demo manually

Create a separate cluster, `discovery-demo`, with RoundRobin balancing and one initial sample backend destination.
Create a route with these fields:

```json
{
  "ClusterId": "discovery-demo",
  "Match": {"Path": "/discovery-demo/{**rest}"},
  "Transforms": [{"PathRemovePrefix": "/discovery-demo"}]
}
```

Add a cluster-scoped `service-discovery` binding:

```json
{
  "enabled": true,
  "mode": "HttpJson",
  "endpoint": "http://localhost:CATALOG_PORT/catalog",
  "refreshSeconds": 5,
  "requestTimeoutSeconds": 1,
  "maxStaleSeconds": 15,
  "stalePolicy": "RejectNewTraffic"
}
```

Replace `CATALOG_PORT` with the assigned catalog port. Reserve names that do not already exist.
The initial destination is a seed required by cluster creation. Discovery replaces its runtime destination set.
Do not attach this binding to the ordinary registration-owned `sample-service` cluster.
The two discovery mechanisms should not compete for the same cluster.

Request `/discovery-demo/api/local-service/instance` through the gateway.
Repeated requests should identify both `backend-one` and `backend-two`.
Destination IDs derive from normalized addresses. Removing and restoring an unchanged address preserves its destination ID.
Aspire can assign new addresses after a backend restart, which correctly creates new destination IDs.

## Change membership and simulate failure

Send local control requests to the catalog origin, using the [HTTP examples](../samples/SampleDiscoveryCatalog/SampleDiscoveryCatalog.http).

| Control body | Expected discovery behavior |
| --- | --- |
| `{"mode":"healthy","backends":["backend-one"]}` | Remove backend-two and retain backend-one's destination identity. |
| `{"mode":"healthy","backends":["backend-one","backend-two"]}` | Add both destinations with their address-derived identities. |
| `{"mode":"malformed","backends":["backend-one","backend-two"]}` | Report failed refreshes and retain fresh destinations temporarily. |
| `{"mode":"unavailable","backends":["backend-one","backend-two"]}` | Report HTTP failure and apply the same stale policy. |
| `{"mode":"healthy","backends":[]}` | Successfully publish an empty destination set. New proxy requests return 503. |

Only configured identifiers are accepted. Invalid controls return HTTP 400 without changing selection.
The catalog bounds backend count and request bodies, and stores no persistent state.
Keep its unauthenticated `/control` endpoints off proxy routes.

With `RejectNewTraffic`, stale discovery eventually publishes an empty runtime set and rejects new requests with HTTP 503.
In-flight requests retain their captured configuration.
Refresh cycles, provider timeouts, backoff, and publication contention affect exact timing.
The fifteen-second limit is a sample policy, not a strict end-to-end deadline.
Restoring healthy mode resumes discovery after its current retry delay.

Inspect the **Service Discovery** plugin resource diagnostics in the administration UI.
Authenticated `GET /wafish/api/plugin-resources/service-discovery` exposes `customStatistics`, including:

- `failedBindings`: bindings with refresh failures.
- `staleBindings`: bindings whose freshness limit has expired.
- `rejectedBindings`: bindings rejecting new traffic because of stale policy.
- `maxStaleAgeSeconds`: the greatest observed freshness age.

Healthy recovery clears failure and stale diagnostics and restores destinations.
Discovery changes active runtime destinations. It does not rewrite persisted cluster destinations or binding revisions.
A successful empty catalog is healthy discovery, even though proxy traffic has no available destination.

## Cleanup

Remove only the demo route, cluster, and binding. Stop the AppHost normally.
Restart without `EnableServiceDiscovery` to remove the catalog resource.
Existing topology data and container volumes remain intact.

See the [catalog source](../samples/SampleDiscoveryCatalog/DiscoveryCatalog.cs), [verification script](../scripts/check-service-discovery.py), and [discovery plugin policy](../src/Wafish.Plugin.ServiceDiscovery/README.md).
