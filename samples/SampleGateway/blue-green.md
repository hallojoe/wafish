# Blue-green deployment

Run an isolated demonstration from the repository root:

```bash
dotnet build samples/SampleGateway/SampleGateway.csproj -c Release
python3 scripts/check-blue-green.py
```

The script creates a private SQLite database, a gateway, and two loopback backends.
It generates an API key in memory and removes its resources when finished.
Existing sample hosts and configuration are not used.

The demonstration uses one route ID, `blue-green`, and two clusters, `blue` and `green`.
Both backends implement `/health` and return their version in response bodies.

1. Check blue's health and activate its configuration.
2. Reject unhealthy green before sending a configuration change.
3. Make green healthy, preview its configuration, and apply the cutover.
4. Send a stale configuration update and verify HTTP 409.
5. Check blue's health and apply the saved route selection to roll back.
6. Read the forwarded response and exported route after each successful change.

All successful stages retain route ID `blue-green`. The output records each persisted configuration version.
The script exits unsuccessfully if any check fails.

## API sequence

Use authenticated requests under `/wafish/api/config`:

- `POST changes/preview` validates a complete candidate without changing active configuration.
- `POST changes` applies it using `ExpectedVersion` and an `Idempotency-Key` header.
- `GET export` confirms the retained route identity and selected cluster.

The isolated demonstration reads the initial version from a stale preview response.
The following successful apply responses provide the next expected version.
Each logical mutation uses a new receipt key. Retrying that mutation should reuse its key and identical body.
A version conflict requires reviewing current configuration and repeating the preview.

Rollback uses the previous desired route selection with the current expected version.
It does not restore an old version counter or overwrite concurrent changes.
The complete candidate includes both clusters throughout the demonstration.

For an existing gateway, preserve all unrelated routes, clusters, and activation settings in the candidate.
The script intentionally supports only its private gateway.

## Operational limits

This demonstrates configuration cutover and rollback, not application data migration.
A health check is a point-in-time check. It cannot guarantee a later request will succeed.
Existing in-flight requests can finish against the previous backend.
Keep blue available until the observation and drain periods finish.
Use application-specific readiness checks, compatible schemas, and authenticated HTTPS for remote administration.
The local test permits HTTP only on loopback listeners.
