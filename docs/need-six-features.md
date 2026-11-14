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
