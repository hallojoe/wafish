# Operate a gateway

Treat configuration, runtime activation, and external dependency health as separate observations.

## Change configuration deliberately

1. Read the persisted revision and current bindings.
2. Prepare the complete desired configuration.
3. Preview it and inspect validation errors.
4. Apply it with the expected revision and an idempotency key.
5. Inspect both persisted and active revisions.

Preview does not reserve a revision. Another writer can cause a conflict before apply.
A repeated identical request can reuse its receipt within the documented retention period.
See [configuration changes](../configuration-changes.md) and [mutation responses](../mutation-contracts.md).

## Diagnose a saved but inactive change

A storage transaction can succeed before runtime activation fails.
Do not report such a result as fully applied.
Inspect the response, active revision, plugin health, and logs before making further changes.
Restart reconciles the persisted configuration according to the documented activation contract.

Versioned [change sets](../configuration-policy-contracts.md) can store configuration, bindings, and receipts atomically in SQLite.
Runtime publication remains a distinct phase.

## Observe the request path

Use route and cluster metrics to locate affected traffic.
Latency distributions describe attempts observed by the metrics middleware, including applicable retries and failures.
Use request logs and audit records to connect runtime behavior with operator changes.
See [metrics](../../src/Wafish.Plugin.Metrics/README.md) and [safe logging](../security.md).

## Maintain a destination

Maintenance windows exclude new traffic from selected destinations.
Existing requests can finish. Recovery probes follow the configured policy.
Windows are process-local and disappear on restart. Apply them to each serving replica when required.
See [maintenance contracts](../configuration-policy-contracts.md).

## Back up state

SQLite backups include committed data and preserve source encryption.
Keep the encryption key available for restoration through your own secret-management process.
Check storage capabilities before requiring encryption or consistent backup.
See [storage and backup contracts](../registration-operations-contracts.md) and [encryption boundaries](../security.md).

## Register and retire instances

Use renewable instance leases when registration ownership must be tracked.
Use readiness to gate service exposure and draining to stop new traffic before removal.
Endpoint failover waits when release of previous ownership is uncertain.
See [registration contracts](../registration-operations-contracts.md) and [ordered endpoints](../configuration-policy-contracts.md).

## Collect diagnostics

The administration diagnostic bundle reports revisions, plugin health, drain state, queue losses, and storage capabilities.
It excludes configuration bodies, credentials, connection strings, and raw exception text.
Collection remains bounded. An uncooperative probe can retain its slot until completion.
See [diagnostic bundle limits](../registration-operations-contracts.md).
