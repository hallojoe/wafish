# R37: trusted tenant quotas

The optional `Tenant` partition selects one authenticated claim with the configured type and issuer.
An explicit allowlist admits at most 256 tenant values per route.
Keys contain a SHA-256 digest of the issuer, claim type, and tenant value.
Caller headers cannot select the tenant.

Missing authentication returns 401. Missing, untrusted, ambiguous, and unlisted claims return 403.
These requests stop before storage, including with `FailOpen`.
The default remains IP partitioning.

Verification covers schema constraints, tenant isolation, shared allowance across client addresses,
rejected-identity churn, malformed raw configuration, and forwarding prevention.
The isolated Redis suite verifies shared tenant quotas across two real store instances.

See `regression.json`, `redis.log`, `api.log`, and `changed-coverage.json` for results.
Run `python3 changed-coverage.py <coverage.xml>` from the repository root to calculate changed-source coverage.

The Release solution build passed. All 298 core regression cases and 19 API baselines passed.
