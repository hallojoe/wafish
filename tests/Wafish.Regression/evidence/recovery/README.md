# Redis and service-discovery recovery

Completed: 2026-11-14. Owner: Codex. Base commit: `b15ef15e10df86d876a5b9099c20b5b504d096c2`.
The tested implementation is in the working tree.

## Completed plan items

- Redis: IMP-01, IMP-02, IMP-04.
- ServiceDiscovery: IMP-01, IMP-02, IMP-04, FEAT-02.

Redis construction failures now use bounded retry delays and one shared replacement task.
Shutdown owns pending connections. Command failures retain normal outage policy and do not replay Lua operations.
Live checks cover all three algorithms, deployment prefixes, replacement, interrupted operations, shared counters, and restart recovery.

Discovery retains separate binding health and uses bounded retry backoff with jitter.
Clock-controlled tests cover stale age, initial failure, retain/reject policies, empty results, recovery, and binding changes.
A real Kestrel/YARP check verifies forwarding, stale rejection with HTTP 503, and recovery.
Provider fixtures cover Static, HttpJson, Consul, Nacos, Eureka, and Kubernetes.
They cover malformed responses, unhealthy endpoints, cancellation, and timeouts where applicable.
Eureka and Kubernetes parsing now execute independently of the Nacos branch.

Build prerequisites exposed incomplete namespace migration.
The protobuf C# namespace and three reflection strings now use `Wafish`.
Protobuf field numbers, RPC names, and the `wafish.v1` package remain unchanged.

## Verification

- Redis and discovery Release builds: zero warnings and errors.
- Regression Release build: zero warnings and errors. [Build output](build.txt).
- Standard regression runner: 159 passing cases, including 19 recovery cases. [Output](regression.txt), [JSON](main-results.json).
- Live Redis 8.2.3: seven passing scenario groups. [Output](redis.txt).
- Changed production C# line coverage: see [coverage report](coverage.txt).
- `git diff --check`: passed.

Commands, from the repository root:

```bash
dotnet build src/Wafish.Plugin.RateLimit.Redis/Wafish.Plugin.RateLimit.Redis.csproj -c Release
dotnet build src/Wafish.Plugin.ServiceDiscovery/Wafish.Plugin.ServiceDiscovery.csproj -c Release
dotnet build tests/Wafish.Regression/Wafish.Regression.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --recovery
WAFISH_REDIS_SERVER=/path/to/redis-server dotnet run --project tests/Wafish.Regression -c Release --no-build -- --redis
```

Coverage combines the standard runner and live Redis execution:

```bash
dotnet tool run dotnet-coverage collect -f xml -o /tmp/recovery-main.xml 'dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll'
WAFISH_REDIS_SERVER=/path/to/redis-server dotnet tool run dotnet-coverage collect -f xml -o /tmp/recovery-redis.xml 'dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --redis'
dotnet tool run dotnet-coverage merge /tmp/recovery-main.xml /tmp/recovery-redis.xml -f xml -o /tmp/recovery.xml
python3 tests/Wafish.Regression/evidence/recovery/changed-coverage.py /tmp/recovery.xml
```

## Compatibility and limits

Existing Redis constructors and failure policies remain available.
Discovery defaults retain last-known destinations without expiry.
`maxStaleSeconds` and `stalePolicy` are additive binding settings.
Invalid provider shapes now fail refresh instead of silently publishing an empty set.
`GetBindingHealth()` adds per-binding freshness diagnostics without exposing provider exception messages.

Stale expiry is periodic. Active provider requests and publication contention can delay enforcement.
Expiry changes runtime destinations only. It does not edit persisted configuration or cancel existing requests.
Tests use standalone Redis on Linux. Redis Cluster and Sentinel promotion are not covered.
Provider tests use controlled HTTP fixtures. They do not require external registry credentials.
