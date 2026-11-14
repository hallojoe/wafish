# Security verification

Owner: Codex. Completed: 2026-11-14.
Base commit: `6ba871a`. Tests include the uncommitted working tree. No commit was created.

## Commands and results

- `dotnet build Wafish.slnx --no-restore`: zero warnings and errors. See build-debug.log.
- `dotnet run --project tests/Wafish.Regression -c Release --no-build`: 64 passing checks. See regression.log.
- `dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build`: 19 passing checks. See administration.log.
- `dotnet list Wafish.slnx package --vulnerable --include-transitive`: no reported package advisories. See dependency-audit.log.
- `ANEIANG_REDIS_SERVER=<local redis-server> dotnet run --project tests/Wafish.Regression --no-build -- --redis`: three live checks pass.
- Production sample starts with an injected secret and rejects missing credentials. Logs contain no injected secret.

Main checks cover authentication, byte limits, streaming, cache isolation, atomic configuration, and native encrypted storage.
Real Kestrel checks verify gzip, Brotli, identity cache hits, and Redis outage enforcement in the proxy pipeline.
The pipeline previously omitted both compression and Redis rate limiting. Both are now included.
Administration checks cover three environments, SignalR, REST, JWT login, and an authenticated MCP listener.
MCP checks include origin rejection, body limits, tools, and listener shutdown.
Additional checks cover certificate validation, two-factor storage failures, and rejected login after state corruption.
Realtime checks receive actual route traffic updates through authenticated SignalR groups.
Redis checks use two independent clients, three algorithms, outages, restart recovery, and cancellation.

Redis 8.10.3 was built from its release source in a temporary directory.
Source archive SHA256: `85ae62e175cc76f638015fa18cb384b549b6808a765425d9e9d47466f69532ea`.
The Redis test starts and stops its own server. No permanent service was installed.

## Historical release blocker — resolved by SEC-14

SQLCipher 4.5.2 community loads SQLite 3.39.2 despite the managed package update.
This predates the CVE-2025-6965 fix. This initially blocked production release. [SEC-14](../sec14/README.md) replaces the retired library and resolves that blocker.
Application exploitability has not been established.
SEC-10 acceptance permits documenting an explicit release blocker; it does not mean this dependency is fixed.
Current native versions and compatibility evidence are recorded under SEC-14.
See [security configuration and advisory sources](../../../../docs/security.md).
Rootless Podman now verifies the sample image build, startup, and published frontend assets.
The Docker daemon was not used. See [UI verification](../ui/README.md).
