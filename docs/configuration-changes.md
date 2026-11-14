# Configuration changes and previews

Use the administration prefix configured by the host. The examples use `/dashboard`.
Authenticate every request. Configuration preview needs `Read`; apply needs `ConfigurationWrite`.
Plugin activation preview needs `PluginManagement`.

1. Read the current configuration and persisted revision through the host's configuration service or export endpoint.
2. Send the complete desired route and cluster lists to `POST api/config/changes/preview`.
3. Inspect `normalized`, `changes`, and `errors`. Invalid references identify their route and missing cluster.
4. Apply the same desired configuration through `POST api/config/changes` with a unique `Idempotency-Key` header.
5. After an uncertain response, repeat the identical request with the same key.

`expectedVersion` must match the persisted revision. Preview also returns `activeVersion` and `persistedVersion`.
It does not reserve the revision. Another writer can invalidate a preview before apply.
Preview changes identify added, removed, and updated routes or clusters. They are not field-level patches.

The lists replace the complete route and cluster configuration. Omitted entries are removed.
Matching IDs retain their stable internal identities and extension metadata.
Use existing rename endpoints when changing an ID while preserving identity.
`routeActivation` optionally maps route IDs to enabled states. Omitted states retain their existing values.
New routes default to enabled. Unknown activation IDs are rejected.
Application configuration retains its existing startup reconciliation behavior after restart.
Plugin bindings are validated against the candidate but are not changed by this workflow.

Requests are limited to 4 MiB and 10,000 entries per list.
Keys contain 1–128 printable characters. HTTP keys are scoped to the authenticated actor.
Custom authentication must provide stable, distinct principal names for distinct actors.
The in-process API uses the supplied key directly; callers must namespace keys when sharing storage.
Fingerprints ignore JSON object property order. Array order remains significant.
Changed payloads with the same key return HTTP 409.

SQLite retains receipts for 24 hours, across restarts, with 10,000 unexpired receipts at most.
The guarantee ends when the receipt expires. Do not retry an uncertain old operation with a new key.
Inspect the current state first. Unsupported storage providers reject the operation without writing.

Successful retries return the original acknowledgement, even when a later operation changed the configuration.
Storage failure returns HTTP 503 without activation. Activation failure returns HTTP 500 with distinct revision fields.
If acknowledgement storage fails after commit, `activeVersion` is null and the result explains the uncertainty.
Retries retain this conservative result without creating another revision.
Restart reconciles a persisted revision that failed activation. Inspect gateway health before further writes.

`POST api/plugins/preview` accepts `enabledPluginIds` and returns health, resource requirements, and errors.
It prepares and disposes a candidate. It does not activate plugins or start background resources.
Null health means no health probe exists. Preparation success does not prove every external dependency is available.
See [request examples](configuration-changes.http).
