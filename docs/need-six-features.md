# R29–R35 feature contracts

## Versioned configuration change sets

`POST /{prefix}/api/config/change-sets/preview` validates a complete desired configuration and binding set.
Preview requires `Read` permission. It returns normalized candidates, identity-level differences, validation errors, and the authenticated actor.
Preview does not write storage, publish configuration, or start runtime resources.

`POST /{prefix}/api/config/change-sets` applies the same request with an `Idempotency-Key` header.
Apply requires both `ConfigurationWrite` and `PluginManagement` permissions.
The server supplies the actor. Receipt keys are scoped to that actor and operation.
Retries return the original acknowledgement for 24 hours. Changed payloads using the same key return conflict.

```json
{
  "configuration": {
    "expectedVersion": 4,
    "routes": [{ "routeId": "catalog", "clusterId": "catalog", "match": { "path": "/catalog/{**rest}" } }],
    "clusters": [{ "clusterId": "catalog", "destinations": { "one": { "address": "https://backend.example/" } } }]
  },
  "expectedBindingRevision": 2,
  "bindings": [{
    "id": "catalog-cache", "pluginId": "response-cache", "scope": 1, "scopeId": "catalog",
    "enabled": true, "schemaVersion": 1, "configJson": "{\"enabled\":true,\"ttlSeconds\":60}"
  }]
}
```

Both lists are complete replacements. Omitted bindings are removed.
Binding `scopeId` names a route or cluster in the candidate, including newly created targets.
The server assigns target UIDs, configuration versions, plugin versions, and timestamps.
Activate plugins separately before enabling their bindings. Unsupported schemas and target references reject the whole change set.

SQLite checks both expected revisions and stores configuration, bindings, and the receipt in one transaction.
Other providers must implement `IConfigurationChangeSetRepository`; unsupported providers reject apply before writing.
`AtomicChangeSets` reports this optional storage capability. Existing repository implementations remain source compatible.

Responses distinguish active and persisted configuration and binding revisions.
An unknown active binding revision is null, including before its first tracked publication.
HTTP 409 reports a stale expected revision. Re-read configuration and bindings before preparing another request.
A storage failure rolls back all records. A postcommit activation failure retains stored changes and reports HTTP 500.
Restart reconciles stored configuration after activation failure. Reusing the receipt never commits a second revision.
Audit records contain the actor, correlation key, outcome, and revisions. They exclude configuration bodies and secrets.

## Ordered registration endpoints

Configure `Gateway:Registration:RegistrationEndpoints` with up to eight trusted gRPC origins, in priority order.
Example: `["https://gateway-one.example", "https://gateway-two.example"]`.
The list replaces single-endpoint selection. It requires gRPC, `InstanceId`, heartbeats, and a 15–3600 second lease.
An empty list preserves `GatewayUrl` and `GrpcUrl` behavior. Each endpoint receives the configured registration credentials.

The client checks capabilities before registration. Unavailable endpoints are tried in order.
A successful endpoint remains selected for registration, renewal, destination updates, drain, and unregister.
The client preserves its instance ID and lease credential. Mutations are serialized.
TLS uses platform certificate validation.

A lost mutation reply can leave ownership uncertain. The client must confirm unregister before selecting another gateway.
Cancellation also retains uncertain ownership. Lease expiry alone does not prove that an outstanding renewal cannot commit later.
If the previous gateway cannot confirm release, failover waits for connectivity to return.
This favors exclusive ownership over availability during network partitions. There is no automatic failback while ownership remains healthy.
`RegistrationEndpoint` exposes the selected endpoint. Readiness remains available through `IRegistrationStatusSource`.

## Destination maintenance

`PUT /{prefix}/api/circuit-breaker/maintenance` accepts `clusterId`, `destinationId`, `reason`, `durationSeconds`, and `allowRecoveryProbes`.
The destination must exist. Duration must be 1–86400 seconds; reason is limited to 1024 printable characters.
The authenticated actor and UTC start/expiry times are recorded.
`GET` lists active windows. `DELETE` cancels one window using `clusterId` and `destinationId` query parameters.
Reads require `Read`; changes require `PluginManagement`.

Windows are process-local and expire using monotonic time. Apply each window to every serving replica when required.
Restart clears windows. At most 10,000 windows can exist per process.
Windows address exact cluster and destination IDs. Rename or replacement requires a new window for the new IDs.

Maintenance filters new proxy traffic and retry candidates, including clusters without circuit-breaker bindings.
Existing requests can finish. Maintenance does not add failures or reset circuit statistics.
`allowRecoveryProbes` permits only eligible half-open probes from an enabled circuit breaker, within its existing concurrency limit.
A closed circuit remains blocked during maintenance. Expiry or cancellation restores normal eligibility.

## Kubernetes EndpointSlice discovery

Use an enabled cluster binding for `service-discovery`:

```json
{
  "enabled": true,
  "mode": "KubernetesEndpointSlice",
  "endpoint": "https://kubernetes.default.svc",
  "namespace": "default",
  "serviceName": "catalog",
  "portName": "http",
  "useServiceAccountToken": true,
  "refreshSeconds": 30,
  "requestTimeoutSeconds": 5,
  "maxStaleSeconds": 120,
  "stalePolicy": "RejectNewTraffic"
}
```

The provider lists `discovery.k8s.io/v1` EndpointSlices using the service-name label.
It follows continuation tokens and publishes only after all pages succeed.
IPv4 and IPv6 addresses are supported. FQDN slices are rejected.
`portName` selects one named TCP port. Omitting it includes all concrete TCP ports; UDP and SCTP are excluded.

Endpoints with `ready:false`, `serving:false`, or `terminating:true` are excluded.
Missing or null readiness/serving conditions mean true; missing or null termination means false.
These defaults follow the [EndpointSlice API](https://kubernetes.io/docs/reference/kubernetes-api/discovery/endpoint-slice-v1/).
Duplicate addresses are removed. Each successful refresh replaces the previous endpoint set, including valid empty results.
Malformed responses, permission failures, and expired pagination retain the last valid set under the configured stale policy.

One refresh permits at most 100 pages, 10,000 address/port pairs, four MiB per page, and sixteen MiB total.
The configured timeout covers the complete refresh. Partial pages are never published.

Service-account authentication is opt-in and requires HTTPS.
The token comes from `/var/run/secrets/kubernetes.io/serviceaccount/token` and is re-read for each refresh.
The default is false. External authentication can use the named `service-discovery` HTTP client.
Install the Kubernetes API CA in the container trust store or configure certificate trust on that named client.
Certificate validation remains enabled. The new mode does not change legacy `Kubernetes` Endpoints behavior.

Grant the gateway service account namespaced list permission:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: wafish-endpoint-discovery
  namespace: default
rules:
  - apiGroups: [discovery.k8s.io]
    resources: [endpointslices]
    verbs: [list]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: wafish-endpoint-discovery
  namespace: default
subjects:
  - kind: ServiceAccount
    name: wafish
    namespace: default
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: Role
  name: wafish-endpoint-discovery
```

Create and assign the `wafish` service account to the gateway workload.
Verification uses controlled HTTP responses. No live Kubernetes cluster is required by the regression suite.
