# Registration and operations contracts

Implemented through tasks R19–R28.

## Instance leases and readiness

Opt in through `AddWafishClient`:

```csharp
services.AddWafishClient(options =>
{
    options.UseGrpcRegistration = true;
    options.InstanceId = "orders-1";
    options.LeaseDurationSeconds = 60;
    options.DrainSeconds = 10;
});
```

Set the gateway URL, route, and destination through the normal client options.
Lease duration accepts 15–3,600 seconds. Zero retains legacy registration.
Leases require gRPC, an instance ID, and enabled heartbeats.
Capability discovery must advertise `instance-leases`. Unsupported servers reject opt-in clients without silent downgrade.

The client generates one random lease credential. The gateway stores only its SHA-256 hash.
Registration, renewal, destination updates, and removal require the current credential for that instance.
The server returns expiry, renewal interval, grace, and drain state.
Expiry uses Unix milliseconds. Renewal uses seconds, with a minimum interval of five seconds.
The grace period is 30 seconds. A reaper checks every five seconds.
An expired instance can therefore remain stored for up to five additional seconds after grace.
Renewal during grace remains valid. Renewal after grace fails.
A replacement gets a new credential. The old credential cannot renew or remove it.
Lease metadata and destinations commit in the same configuration transaction and survive restart.

Drain removes the instance from new routing snapshots immediately.
Requests using earlier snapshots can finish. Client shutdown waits up to `DrainSeconds` before unregistering.
This value accepts 0–300 seconds. The host shutdown cancellation token can shorten the wait.
A draining lease cannot become active through registration or destination updates.
Unregister it before immediate replacement, or wait for expiry.
Startup registration failures continue through the existing heartbeat recovery loop.

Legacy clients retain non-expiring registration. Their destinations need explicit removal.
Legacy registration and removal cannot replace a live leased registration.
Authenticated full-configuration administration remains a trusted override.
Use one destination owner per cluster. Do not combine leased registration with a service-discovery binding.
Lease credentials protect instance ownership within the existing gateway authentication boundary.
They do not implement tenant isolation or multiple-gateway failover.

`IRegistrationStatusSource.GetRegistrationStatus()` reads local state without network calls.
Transient renewal failures preserve readiness until expiry plus grace. Draining and unregistering fail readiness immediately.
For sample backends, set `Sample:RegistrationReadiness=true`.
The sample `/health` check then requires registration. `/alive` remains independent.
The option defaults to false. Missing client registration fails the enabled readiness check.

## Latency distributions

`PluginMetricSnapshot.Latency` has 13 disjoint buckets. Durations use seconds.
Upper bounds are inclusive: 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, and 10.
The final null bound means greater than 10 seconds.
Snapshots copy bucket counts. No individual request durations remain stored.
The existing 10,000-series bound and identity retirement apply.

`wafish.route.latency` and `wafish.cluster.latency` are histogram instruments with unit `s`.
They use the existing identity tags. Collectors choose their aggregation boundaries.
Use the documented boundaries when collector buckets must match administration snapshots.
`GET /api/metrics/latency` returns route and cluster distributions.
Measurements describe attempts reaching the metrics middleware, including retries, errors, and cancellation.

## Cache invalidation

Response-cache bindings accept `tags`, with at most eight unique strings of 1–64 characters.
`POST /api/cache/purge` requires PluginManagement permission.
Supply `routeId`, `tag`, or both. Both selectors mean intersection.
Route IDs compare without case differences. Tags compare exactly.
Each capture reserves a 4-KiB metadata allowance plus the existing header and body allowances.
Unrelated cached entries remain available. In-flight readers retain their charged buffers until completion.
A purge fences pending fills, including unrelated pending fills, to prevent stale insertion.
The next eligible request can refill those entries.
Purge records the actor, selectors, and removed count in the configuration audit.

## WAF evaluation and exclusions

Blocking remains the default. Route bindings can set `detectionOnlyRules`:

```json
{
  "detectionOnlyRules": ["sql-injection"],
  "exclusions": [
    { "ruleId": "sql-injection", "target": "Json", "field": "/example" }
  ]
}
```

Stable selectable IDs are `sql-injection`, `xss`, `path-traversal`, and `ip-access`.
Detection logs identify the rule, route, trace, and non-blocking decision. They omit request content.
Request limits, decoding failures, and scanner failures still block.

Exclusions apply only to `sql-injection` or `xss` within their route binding.
Targets are `Query`, `Form`, and `Json`. Fields compare exactly, including case.
Query and form fields name decoded keys. JSON fields use JSON Pointer paths, including numeric array indexes.
Only scalar values can be excluded. Keys, sibling fields, and other rules remain inspected.
Object or array exclusions fail with HTTP 400. Malformed structured bodies also fail closed.
The original forwarded body remains unchanged.

At most 32 exclusions are accepted. Field names have a 256-character limit; wildcards are forbidden.
JSON parsing has a maximum depth of 32. Existing request-byte and UTF-8 limits remain mandatory.
`POST /api/plugin-bindings/preview` validates proposed binding configuration without storage or activation.
Complete-configuration previews include active route WAF exclusions in `wafExclusions`.
Binding create, update, activation, and deletion audit changed exclusions, including batch operations.
Trusted code that writes directly to storage must supply its own audit policy.

## Plugin drain diagnostics

`IPluginDrainDiagnostics` and `GET /api/plugins/drain` report domain generations and active requests.
Retired domains report retirement age and `Draining` or `Disposing` state.
Successful disposal removes the domain. Failed disposal retains a safe diagnostic record.
At most 100 failure records remain. They contain no providers, plugin instances, or exception messages.
The runtime still waits for its owned retirement tasks during shutdown.

## Storage capabilities and backups

`IStorageCapabilitiesProvider` describes encryption, consistent backup, atomic configuration, atomic bindings, and configuration receipts.
It also reports whether encryption is enabled.
Hosts can reject unsupported deployment requirements before accepting requests:

```csharp
services.AddWafishStorageRequirements(options =>
{
    options.Required = StorageFeature.ConsistentBackup | StorageFeature.AtomicConfiguration;
    options.RequireEncryptionEnabled = true;
});
```

Providers must describe their complete installed repository implementation accurately.
Custom providers must replace SQLite capability and backup registrations when replacing SQLite storage.
Missing capability metadata fails required-feature validation. No requirements preserve legacy startup behavior.

`IStorageBackupProvider.CreateBackupAsync` returns an owned `StorageBackup`.
Callers must dispose it to close the stream and remove temporary resources.
SQLite creates a consistent backup from committed data, including WAL contents.
Encryption follows the source database. Backups require the same key.
Temporary data uses a private directory. Cancellation and failures remove incomplete files.
SQLite cancellation remains subject to native command execution boundaries.

Administration requests backups through this contract, without parsing connection strings or issuing provider SQL.
Unsupported providers return HTTP 501. Provider failures return HTTP 503.
Backup downloads retain CredentialManagement permission.

## Diagnostic bundles

`GET /api/diagnostics/bundle` requires Read permission and returns a JSON attachment.
It includes build version, active and persisted revisions, plugin order and health, drain state, queue losses, and storage capabilities.
Each collection gets a new correlation ID.
Configuration bodies, credentials, connection strings, exception text, health messages, and custom statistics are excluded.
Plugin IDs remain included, with a 256-character limit. Do not put secrets in plugin identifiers.

Collection has a two-second deadline and a 256-KiB output limit.
Plugin and drain lists have a 100-item limit. Missing or failed sections produce explicit failure codes.
Only one collection can own probes at a time.
A provider that ignores cancellation keeps that slot until it completes. Later calls return `collection-busy`.
This prevents repeated timeouts from creating unbounded background work.
Plugins must still implement cooperative health probes. A held probe retains its runtime-domain lease until completion.

Paths above are relative to the configured administration base path.
See [HTTP examples](registration-operations.http) and [verification](../tests/Wafish.Regression/evidence/need-seven/README.md).
