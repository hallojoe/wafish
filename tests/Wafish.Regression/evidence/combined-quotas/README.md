# R38: combined Redis quotas verification

Verified on Linux with .NET SDK 10.0.403 and isolated Redis 8.6.7 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 292 main regression cases passed. See [results](regression.json).
- Changed executable source coverage: 88.89% (16/18). See [coverage](changed-coverage.json).
- Focused policy, schema, cancellation, and optional-store checks passed. See [results](focused.json).
- Live Redis integration passed. See [results](live.json) and [details](live.log).
- All 19 package API compatibility checks passed against unchanged reviewed snapshots. See [results](public-api.log).

Live checks use two gateway store instances and one isolated, loopback-only Redis process.
They verify concurrent burst admission, sustained rejection without partial consumption, window resets, expiry, policy replacement, and middleware sharing.
Existing single-quota algorithms, connection replacement, outages, recovery, and cancellation remain covered.
The test controls only its private Redis state. It does not change any existing Redis service.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --redis
```

Set `WAFISH_REDIS_SERVER` to an installed Redis server executable for the live command.

Compatibility: existing algorithms retain their `v2` keys and store interface.
The additive `ICombinedDistributedRateLimitStore` capability supplies atomic acquisition for `DualWindow` bindings.
The additive execution-plan property is `BurstLimit`. No reviewed API declaration was removed or changed.
New combined policies use `v3` keys; changing allowances starts fresh counters while old keys expire.
Both windows occupy one hash key. Cluster slot safety follows from that single-key design; a live cluster was not required.
